using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Native;
using SkiaSharp;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Loads lightweight catalog evidence and reuses versioned deep glyph evidence for unchanged candidate files.</summary>
public sealed class PhysicalFontAnalysisEvidenceSource : IFontAnalysisEvidenceSource, IFontDeepAnalysisEvidenceSource
{
    internal static readonly string DeepEvidenceKey = string.Join('|', "deep-v7", Environment.Version, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture, typeof(SKTypeface).Assembly.GetName().Version, HarfBuzzSubsetNative.GetNativeVersion(), FreeTypeFontVerifier.IsAvailable, LibassFontVerifier.IsAvailable);
    internal static readonly string AnalysisCompatibilityKey = FontDuplicateAnalyzer.CurrentEvidenceVersion + "|" + DeepEvidenceKey;

    private readonly IFontAnalysisEvidenceRepository _repository;
    private readonly Func<string, CancellationToken, Task<byte[]>> _readFile;
    private readonly Func<byte[], int, InspectedFontFace, FontVerificationStatus> _verifyFreeType;
    private readonly Func<byte[], InspectedFontFace, FontVerificationStatus> _verifyLibass;
    private InventorySnapshot? _inventorySnapshot;

    /// <summary>Initializes a new instance of the <see cref="PhysicalFontAnalysisEvidenceSource"/> class.</summary>
    /// <param name="repository">The durable staged and published font inventory.</param>
    public PhysicalFontAnalysisEvidenceSource(IFontAnalysisEvidenceRepository repository)
        : this(repository, File.ReadAllBytesAsync)
    {
    }

    internal PhysicalFontAnalysisEvidenceSource(
        IFontAnalysisEvidenceRepository repository,
        Func<string, CancellationToken, Task<byte[]>> readFile,
        Func<byte[], int, InspectedFontFace, FontVerificationStatus>? verifyFreeType = null,
        Func<byte[], InspectedFontFace, FontVerificationStatus>? verifyLibass = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _readFile = readFile ?? throw new ArgumentNullException(nameof(readFile));
        _verifyFreeType = verifyFreeType ?? FreeTypeFontVerifier.Verify;
        _verifyLibass = verifyLibass ?? LibassFontVerifier.Verify;
    }

    /// <summary>Loads lightweight candidate-bucketing evidence belonging exclusively to the requested generation.</summary>
    /// <param name="generationId">The inventory generation to analyze.</param>
    /// <param name="cancellationToken">A token that cancels catalog access.</param>
    /// <returns>The immutable lightweight analysis and retention snapshot.</returns>
    public async Task<FontAnalysisSnapshot> LoadAsync(long generationId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationId);
        IReadOnlyList<FontFileRecord> inventory = await _repository.LoadInventoryAsync(generationId, cancellationToken).ConfigureAwait(false);
        if (inventory.Any(file => file.GenerationId != generationId))
        {
            throw new InvalidOperationException("The requested inventory generation contains mismatched file records.");
        }

        Volatile.Write(ref _inventorySnapshot, new InventorySnapshot(
            generationId,
            inventory.ToDictionary(file => file.Id)));
        FontFileRecord[] generationFiles = inventory.Where(file => file.Faces.Count > 0).ToArray();
        var faces = new List<FontFaceEvidence>(generationFiles.Sum(file => file.Faces.Count));
        var retentionFiles = new List<FontRetentionFile>(generationFiles.Length);
        foreach (FontFileRecord file in generationFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FontRetentionFace[] retentionFaces = file.Faces
                .Select(face => new FontRetentionFace(
                    face.Id,
                    face.Technology,
                    file.Health == FontFileHealth.Healthy,
                    new FontRetentionQualityEvidence(new HashSet<FontQualitySignal>(), null)))
                .ToArray();
            foreach (FontFaceRecord face in file.Faces)
            {
                faces.Add(LightweightFace(file, face));
            }

            retentionFiles.Add(RetentionFile(file, retentionFaces));
        }

        return new FontAnalysisSnapshot(faces, new DirectoryPolicySnapshot(retentionFiles));
    }

    /// <summary>Loads complete comparison evidence only for selected candidate physical files, reusing verified cache entries.</summary>
    /// <param name="generationId">The staged or published inventory generation.</param>
    /// <param name="fileIds">The distinct candidate physical-file identifiers.</param>
    /// <param name="cancellationToken">A token that cancels file reading or glyph evidence construction.</param>
    /// <returns>The replacement deep Face and file-quality evidence.</returns>
    public async Task<FontDeepAnalysisEvidence> LoadFilesAsync(
        long generationId,
        IReadOnlyList<long> fileIds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationId);
        ArgumentNullException.ThrowIfNull(fileIds);
        HashSet<long> requested = fileIds.ToHashSet();
        if (requested.Count != fileIds.Count || requested.Any(fileId => fileId <= 0))
        {
            throw new ArgumentException("Candidate physical-file identifiers must be distinct and positive.", nameof(fileIds));
        }

        InventorySnapshot? snapshot = Volatile.Read(ref _inventorySnapshot);
        if (snapshot is null || snapshot.GenerationId != generationId)
        {
            IReadOnlyList<FontFileRecord> inventory = await _repository.LoadInventoryAsync(generationId, cancellationToken).ConfigureAwait(false);
            if (inventory.Any(file => file.GenerationId != generationId))
            {
                throw new InvalidOperationException("The requested inventory generation contains mismatched file records.");
            }

            snapshot = new InventorySnapshot(generationId, inventory.ToDictionary(file => file.Id));
            Volatile.Write(ref _inventorySnapshot, snapshot);
        }

        FontFileRecord[] selected = requested
            .Where(snapshot.Files.ContainsKey)
            .Select(fileId => snapshot.Files[fileId])
            .OrderBy(file => file.Id)
            .ToArray();
        if (selected.Length != requested.Count)
        {
            throw new InvalidOperationException("One or more candidate physical files are absent from the requested inventory.");
        }

        var faces = new List<FontFaceEvidence>(selected.Sum(file => file.Faces.Count));
        var retentionFiles = new List<FontRetentionFile>(selected.Length);
        foreach (FontFileRecord file in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileEvidence evidence = await LoadOrInspectFileAsync(file, cancellationToken).ConfigureAwait(false);
            faces.AddRange(evidence.Faces);
            retentionFiles.Add(evidence.RetentionFile);
        }

        return new FontDeepAnalysisEvidence(faces, retentionFiles);
    }

    private async Task<FileEvidence> LoadOrInspectFileAsync(FontFileRecord file, CancellationToken cancellationToken)
    {
        var info = new FileInfo(file.Path);
        if (info.Exists && info.Length == file.Size && new DateTimeOffset(info.LastWriteTimeUtc) == file.ModifiedUtc)
        {
            string? payload = await _repository.LoadReusableDeepEvidenceAsync(file, DeepEvidenceKey, cancellationToken).ConfigureAwait(false);
            if (payload is not null)
            {
                try
                {
                    FileEvidence? rebound = RebindCachedEvidence(file, FontDeepEvidenceJson.Deserialize(payload));
                    if (rebound is not null)
                    {
                        return rebound;
                    }
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or NotSupportedException or DecoderFallbackException)
                {
                    // A malformed derived cache entry cannot authorize a relationship.
                }
            }
        }

        FileEvidence inspected = await InspectFileAsync(file, cancellationToken).ConfigureAwait(false);
        if (inspected.Faces.Count > 0 && inspected.Faces.All(face => face.IsDeepEvidenceComplete))
        {
            string payload = FontDeepEvidenceJson.Serialize(new FontDeepAnalysisEvidence(inspected.Faces, [inspected.RetentionFile]));
            await _repository.SaveDeepEvidenceAsync(file.Id, DeepEvidenceKey, payload, cancellationToken).ConfigureAwait(false);
        }

        return inspected;
    }

    private static FileEvidence? RebindCachedEvidence(FontFileRecord file, FontDeepAnalysisEvidence cached)
    {
        if (cached.Files.Count != 1 || cached.Faces.Count != file.Faces.Count || cached.Files[0].Faces.Count != file.Faces.Count)
        {
            return null;
        }

        FontFaceEvidence[] cachedFaces = cached.Faces.OrderBy(face => face.FaceIndex).ToArray();
        FontFaceRecord[] currentFaces = file.Faces.OrderBy(face => face.FaceIndex).ToArray();
        FontRetentionFace[] cachedRetention = cached.Files[0].Faces.OrderBy(face => face.FaceId).ToArray();
        if (cachedFaces.Select(face => face.FaceId).Order().SequenceEqual(cachedRetention.Select(face => face.FaceId).Order()) is false)
        {
            return null;
        }

        var reboundFaces = new FontFaceEvidence[currentFaces.Length];
        var reboundRetention = new FontRetentionFace[currentFaces.Length];
        for (int index = 0; index < currentFaces.Length; index++)
        {
            FontFaceRecord current = currentFaces[index];
            FontFaceEvidence previous = cachedFaces[index];
            FontRetentionFace? quality = cachedRetention.FirstOrDefault(face => face.FaceId == previous.FaceId);
            if (quality is null || !previous.IsDeepEvidenceComplete || !previous.IsClassifiable
                || previous.FaceIndex != current.FaceIndex
                || previous.PhysicalFileId != file.PhysicalIdentity
                || !previous.FileHash.Span.SequenceEqual(file.Sha256.Span)
                || !previous.StrictFingerprint.Span.SequenceEqual(current.StrictFingerprint.Span))
            {
                return null;
            }

            string? verificationError = FunctionalVerificationError(quality.Quality.Verification);
            bool classifiable = verificationError is null;
            reboundFaces[index] = previous with
            {
                FaceId = current.Id,
                IsClassifiable = classifiable,
                IsDeepEvidenceComplete = classifiable,
                ClassificationError = verificationError,
                FileId = file.Id,
                Identity = FontIdentityResolver.WithNameRoles(previous.Identity, current.NameRecords)
            };
            reboundRetention[index] = quality with { FaceId = current.Id, IsClassifiable = classifiable };
        }

        return new FileEvidence(reboundFaces, RetentionFile(file, reboundRetention));
    }

    private async Task<FileEvidence> InspectFileAsync(FontFileRecord file, CancellationToken cancellationToken)
    {
        try
        {
            byte[] data = await _readFile(file.Path, cancellationToken).ConfigureAwait(false);
            if (data.LongLength != file.Size || !SHA256.HashData(data).AsSpan().SequenceEqual(file.Sha256.Span))
            {
                return Unclassifiable(file, "Physical file evidence changed after inventory publication.");
            }

            OpenTypeContainer container = OpenTypeFontReader.Read(data);
            Dictionary<int, OpenTypeFace> sourceFaces = container.Faces.ToDictionary(face => face.FaceIndex);
            var analysisFaces = new List<FontFaceEvidence>(file.Faces.Count);
            var retentionFaces = new List<FontRetentionFace>(file.Faces.Count);
            foreach (FontFaceRecord record in file.Faces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!sourceFaces.TryGetValue(record.FaceIndex, out OpenTypeFace? sourceFace))
                {
                    AddUnclassifiableFace(file, record, "The published Face index no longer exists.", analysisFaces, retentionFaces);
                    continue;
                }

                InspectedFontFace inspected = FontFaceInspector.Inspect(sourceFace);
                FontFaceFingerprints fingerprints = FontFingerprintBuilder.BuildFace(sourceFace);
                if (!inspected.IsClassifiable || !fingerprints.IsSemanticEvidenceComplete || !fingerprints.StrictFingerprint.Span.SequenceEqual(record.StrictFingerprint.Span))
                {
                    AddUnclassifiableFace(
                        file,
                        record,
                        inspected.Error ?? SemanticDiagnostic(sourceFace, inspected, fingerprints, record),
                        analysisFaces,
                        retentionFaces);
                    continue;
                }

                Dictionary<uint, ReadOnlyMemory<byte>>? characters = BuildCharacterFingerprints(
                    data,
                    record.FaceIndex,
                    inspected,
                    cancellationToken,
                    out Dictionary<ulong, ReadOnlyMemory<byte>> variations,
                    out string? mappingError);
                FontIdentityEvidence identity = FontIdentityResolver.Resolve(inspected);
                Dictionary<string, FontVerificationStatus> verification = FontQualityVerifier.Verify(data, sourceFace, inspected, characters is not null, _verifyFreeType, _verifyLibass);
                string? verificationError = FunctionalVerificationError(verification);
                bool classifiable = characters is not null && verificationError is null;
                analysisFaces.Add(new FontFaceEvidence
                {
                    FaceId = record.Id,
                    FileId = file.Id,
                    IsClassifiable = classifiable,
                    ClassificationError = classifiable ? null : string.Join(" ", new[] { mappingError, verificationError }.Where(error => error is not null)),
                    IsDeepEvidenceComplete = classifiable,
                    PhysicalFileId = file.PhysicalIdentity,
                    FaceIndex = record.FaceIndex,
                    FileHash = file.Sha256,
                    StrictFingerprint = fingerprints.StrictFingerprint,
                    SemanticFingerprints = fingerprints.SemanticFingerprints,
                    Identity = identity,
                    Technology = inspected.Technology,
                    UnitsPerEm = inspected.UnitsPerEm,
                    IsFixedPitch = inspected.IsFixedPitch,
                    UnicodeCoverage = inspected.UnicodeCoverage,
                    VariationSelectors = inspected.VariationSelectors.ToHashSet(),
                    VariationFingerprints = variations,
                    CharacterFingerprints = characters ?? new Dictionary<uint, ReadOnlyMemory<byte>>()
                });
                retentionFaces.Add(new FontRetentionFace(
                    record.Id,
                    inspected.Technology,
                    classifiable,
                    BuildQuality(inspected, identity, classifiable, verification)));
            }

            return new FileEvidence(analysisFaces, RetentionFile(file, retentionFaces));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Unclassifiable(file, exception.Message);
        }
    }

    private static string? FunctionalVerificationError(IReadOnlyDictionary<string, FontVerificationStatus> verification)
    {
        string[] requiredNative = ["skia", "harfbuzz", "freetype", "libass"];
        string[] capabilities = ["layout", "vertical", "color", "axes"];
        var failures = new List<string>();
        foreach (string check in requiredNative.Concat(capabilities))
        {
            FontVerificationStatus status = verification.TryGetValue(check, out FontVerificationStatus value)
                ? value
                : FontVerificationStatus.Unknown;
            if (status != FontVerificationStatus.Passed
                && !(capabilities.Contains(check, StringComparer.Ordinal) && status == FontVerificationStatus.NotApplicable))
            {
                failures.Add($"{check}={status}");
            }
        }

        return failures.Count == 0 ? null : "Functional verification incomplete: " + string.Join(", ", failures) + ".";
    }

    private static string SemanticDiagnostic(OpenTypeFace sourceFace, InspectedFontFace inspected, FontFaceFingerprints fingerprints, FontFaceRecord record)
    {
        if (!fingerprints.StrictFingerprint.Span.SequenceEqual(record.StrictFingerprint.Span))
        {
            return "The published strict Face fingerprint differs from the current font content.";
        }

        if (!fingerprints.IsSemanticEvidenceComplete
            && sourceFace.Tables.ContainsKey(OpenTypeTag.FromString("glyf"))
            && !FontGlyphCanonicalizer.TryBuild(sourceFace, out _, out _, out string? glyphError))
        {
            return "TrueType outline parsing failed: " + (glyphError ?? "unknown reason");
        }

        return inspected.IsMappingComplete
            ? "Complete semantic parsing could not be verified."
            : "OpenType cmap mappings are incomplete or unsupported.";
    }

    private static Dictionary<uint, ReadOnlyMemory<byte>>? BuildCharacterFingerprints(
        byte[] data,
        int faceIndex,
        InspectedFontFace inspected,
        CancellationToken cancellationToken,
        out Dictionary<ulong, ReadOnlyMemory<byte>> variations,
        out string? error)
    {
        variations = new Dictionary<ulong, ReadOnlyMemory<byte>>();
        error = null;
        using SKData nativeData = SKData.CreateCopy(data);
        using SKTypeface? typeface = SKTypeface.FromData(nativeData, faceIndex);
        if (typeface is null)
        {
            error = "Skia could not open this Face.";
            return null;
        }

        if (!inspected.IsMappingComplete)
        {
            error = "OpenType cmap mappings are incomplete or unsupported.";
            return null;
        }

        float emSize = inspected.UnitsPerEm > 0 ? inspected.UnitsPerEm : 1000;
        using var font = new SKFont(typeface, emSize);
        using var paint = new SKPaint();
        var glyphCache = new Dictionary<ushort, ReadOnlyMemory<byte>>();
        var characters = new Dictionary<uint, ReadOnlyMemory<byte>>();
        foreach (UnicodeRange range in inspected.UnicodeCoverage)
        {
            for (uint codePoint = range.Start; codePoint <= range.End; codePoint++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ushort glyph = typeface.GetGlyph(checked((int)codePoint));
                if (!inspected.UnicodeMappings.TryGetValue(codePoint, out ushort parsedGlyph) || glyph == 0 || glyph != parsedGlyph)
                {
                    error = $"U+{codePoint:X4}: Skia glyph {glyph} differs from OpenType cmap glyph {parsedGlyph}.";
                    return null;
                }

                if (!glyphCache.TryGetValue(glyph, out ReadOnlyMemory<byte> fingerprint))
                {
                    fingerprint = FingerprintGlyph(font, paint, glyph);
                    glyphCache.Add(glyph, fingerprint);
                }

                characters.Add(codePoint, fingerprint);
                if (codePoint == uint.MaxValue)
                {
                    break;
                }
            }
        }

        foreach (UnicodeVariationMapping mapping in inspected.VariationMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ushort glyph = mapping.GlyphId ?? typeface.GetGlyph(checked((int)mapping.CodePoint));
            if (glyph == 0 || glyph >= typeface.GlyphCount)
            {
                error = $"U+{mapping.CodePoint:X4} variation selector U+{mapping.Selector:X4}: glyph {glyph} is outside the Skia glyph range.";
                return null;
            }

            if (!glyphCache.TryGetValue(glyph, out ReadOnlyMemory<byte> fingerprint))
            {
                fingerprint = FingerprintGlyph(font, paint, glyph);
                glyphCache.Add(glyph, fingerprint);
            }

            variations.Add(((ulong)mapping.Selector << 21) | mapping.CodePoint, fingerprint);
        }

        return characters;
    }

    private static ReadOnlyMemory<byte> FingerprintGlyph(SKFont font, SKPaint paint, ushort glyph)
    {
        Span<ushort> glyphs = stackalloc ushort[1] { glyph };
        Span<float> widths = stackalloc float[1];
        Span<SKRect> bounds = stackalloc SKRect[1];
        font.GetGlyphWidths(glyphs, widths, bounds, paint);
        Span<byte> metrics = stackalloc byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(metrics.Slice(0, 4), BitConverter.SingleToInt32Bits(widths[0]));
        BinaryPrimitives.WriteInt32LittleEndian(metrics.Slice(4, 4), BitConverter.SingleToInt32Bits(bounds[0].Left));
        BinaryPrimitives.WriteInt32LittleEndian(metrics.Slice(8, 4), BitConverter.SingleToInt32Bits(bounds[0].Top));
        BinaryPrimitives.WriteInt32LittleEndian(metrics.Slice(12, 4), BitConverter.SingleToInt32Bits(bounds[0].Right));
        BinaryPrimitives.WriteInt32LittleEndian(metrics.Slice(16, 4), BitConverter.SingleToInt32Bits(bounds[0].Bottom));
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(metrics);
        using SKPath? glyphPath = font.GetGlyphPath(glyph);
        if (glyphPath is not null)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(glyphPath.ToSvgPathData()));
        }

        return hash.GetHashAndReset();
    }

    private static FontRetentionQualityEvidence BuildQuality(
        InspectedFontFace inspected,
        FontIdentityEvidence identity,
        bool characterEvidenceComplete,
        Dictionary<string, FontVerificationStatus> verification)
    {
        var signals = new HashSet<FontQualitySignal>
        {
            FontQualitySignal.ValidTables,
            FontQualitySignal.CompatibleTechnology
        };
        Add(signals, FontQualitySignal.CommonGlyphContentCorrect, characterEvidenceComplete);
        Add(signals, FontQualitySignal.CoverageComplete, characterEvidenceComplete);
        Add(signals, FontQualitySignal.ValidCmap, inspected.ValidCmapCount > 0);
        Add(signals, FontQualitySignal.ValidChecksums, verification["checksums"] == FontVerificationStatus.Passed);
        Add(signals, FontQualitySignal.LayoutComplete, verification["layout"] == FontVerificationStatus.Passed);
        Add(signals, FontQualitySignal.ColorGlyphsComplete, verification["color"] == FontVerificationStatus.Passed);
        Add(signals, FontQualitySignal.VariationAxesComplete, verification["axes"] == FontVerificationStatus.Passed);
        Add(
            signals,
            FontQualitySignal.StableNativeLoaders,
            verification["skia"] == FontVerificationStatus.Passed
            && verification["harfbuzz"] == FontVerificationStatus.Passed
            && verification["freetype"] == FontVerificationStatus.Passed
            && verification["libass"] == FontVerificationStatus.Passed);
        Add(signals, FontQualitySignal.VerticalMetricsComplete, verification["vertical"] == FontVerificationStatus.Passed);
        Add(signals, FontQualitySignal.EmbeddingRights, verification["embeddingRights"] == FontVerificationStatus.Passed);
        Add(signals, FontQualitySignal.VariationSelectorsComplete, characterEvidenceComplete && inspected.IsMappingComplete && inspected.VariationMappings.Count > 0);
        Add(signals, FontQualitySignal.ReliableVersion, identity.Version.IsReliable);
        Add(signals, FontQualitySignal.ConsistentVersionSources, identity.Version.IsReliable && identity.Version.Sources.Count > 1);
        Add(signals, FontQualitySignal.CoherentVendor, identity.VendorId is not null);
        Add(signals, FontQualitySignal.PostScriptName, identity.PostScriptName is not null);
        Add(signals, FontQualitySignal.TypographicNames, identity.FamilyNameId == 16 && identity.SubfamilyNameId == 17);
        Add(signals, FontQualitySignal.WwsNames, identity.FamilyNameId == 21 && identity.SubfamilyNameId == 22);
        Add(signals, FontQualitySignal.LegacyNames, identity.FamilyNameId == 1 && identity.SubfamilyNameId == 2);
        Add(signals, FontQualitySignal.FullName, identity.FullName is not null);
        Add(signals, FontQualitySignal.UniqueId, identity.UniqueId is not null);
        Add(signals, FontQualitySignal.AliasesComplete, inspected.Names.Count > 0);
        return new FontRetentionQualityEvidence(signals, identity.Version.Value) { Verification = verification };
    }

    private static void Add(HashSet<FontQualitySignal> signals, FontQualitySignal signal, bool condition)
    {
        if (condition)
        {
            signals.Add(signal);
        }
    }

    private static FileEvidence Unclassifiable(FontFileRecord file, string reason)
    {
        var faces = new List<FontFaceEvidence>(file.Faces.Count);
        var retentionFaces = new List<FontRetentionFace>(file.Faces.Count);
        foreach (FontFaceRecord record in file.Faces)
        {
            AddUnclassifiableFace(file, record, reason, faces, retentionFaces);
        }

        return new FileEvidence(faces, RetentionFile(file, retentionFaces));
    }

    private static FontFaceEvidence LightweightFace(FontFileRecord file, FontFaceRecord face)
    {
        return new FontFaceEvidence
        {
            FaceId = face.Id,
            FileId = file.Id,
            IsClassifiable = file.Health == FontFileHealth.Healthy,
            ClassificationError = file.Error,
            IsDeepEvidenceComplete = false,
            PhysicalFileId = file.PhysicalIdentity,
            FaceIndex = face.FaceIndex,
            FileHash = file.Sha256,
            StrictFingerprint = face.StrictFingerprint,
            Identity = FallbackIdentity(face),
            Technology = face.Technology,
            UnitsPerEm = face.UnitsPerEm,
            IsFixedPitch = face.IsFixedPitch
        };
    }

    private static void AddUnclassifiableFace(
        FontFileRecord file,
        FontFaceRecord record,
        string reason,
        List<FontFaceEvidence> faces,
        List<FontRetentionFace> retentionFaces)
    {
        FontIdentityEvidence identity = FallbackIdentity(record);
        faces.Add(new FontFaceEvidence
        {
            FaceId = record.Id,
            FileId = file.Id,
            IsClassifiable = false,
            ClassificationError = reason,
            IsDeepEvidenceComplete = false,
            PhysicalFileId = file.PhysicalIdentity,
            FaceIndex = record.FaceIndex,
            FileHash = file.Sha256,
            StrictFingerprint = record.StrictFingerprint,
            Identity = identity,
            Technology = record.Technology,
            UnitsPerEm = record.UnitsPerEm,
            IsFixedPitch = record.IsFixedPitch
        });
        retentionFaces.Add(new FontRetentionFace(
            record.Id,
            record.Technology,
            false,
            new FontRetentionQualityEvidence(new HashSet<FontQualitySignal>(), null)));
    }

    private static FontIdentityEvidence FallbackIdentity(FontFaceRecord face)
    {
        string family = FontIdentityResolver.NormalizeValue(face.FamilyName);
        string style = FontIdentityResolver.NormalizeValue(face.StyleName);
        return new FontIdentityEvidence(
            null,
            family,
            style,
            0,
            0,
            null,
            null,
            family + "\u001f" + style,
            face.Names.Select(FontIdentityResolver.NormalizeValue).ToHashSet(StringComparer.Ordinal),
            null,
            face.Weight,
            face.Width,
            face.Slant,
            new FontVersionEvidence(null, false, []));
    }

    private static FontRetentionFile RetentionFile(FontFileRecord file, IReadOnlyList<FontRetentionFace> faces)
    {
        return new FontRetentionFile
        {
            FileId = file.Id,
            Path = file.Path,
            NormalizedPath = file.NormalizedPath,
            Size = file.Size,
            ModifiedUtc = file.ModifiedUtc,
            DirectoryPolicy = file.DirectoryPolicy,
            LinkKind = file.LinkKind,
            Health = file.Health,
            Faces = faces
        };
    }

    private sealed record InventorySnapshot(long GenerationId, IReadOnlyDictionary<long, FontFileRecord> Files);

    private sealed record FileEvidence(
        IReadOnlyList<FontFaceEvidence> Faces,
        FontRetentionFile RetentionFile);
}
