using System.Buffers.Binary;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Order;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Benchmarks;

/// <summary>Measures a compositional end-to-end estimate for resolution plus the real native subset operation.</summary>
[MemoryDiagnoser]
[MedianColumn]
[Orderer(SummaryOrderPolicy.Default, MethodOrderPolicy.Alphabetical)]
public class SubsettingOverheadBenchmarks
{
    private byte[] _fontBytes = null!;
    private RuntimeFontFace _directFace = null!;
    private Dictionary<string, RuntimeFontFace> _directLookup = null!;
    private RuntimeFontIndex _catalogRuntimeIndex = null!;
    private Func<RuntimeFontFace> _directResolver = null!;
    private Func<RuntimeFontFace> _catalogResolver = null!;
    private FontDescriptor _request = null!;
    private uint[] _codepoints = null!;

    /// <summary>Creates the deterministic generated font and proves both complete paths outside timed methods.</summary>
    [GlobalSetup]
    public void Setup()
    {
        SubsettingFixture fixture = SubsettingFixture.Create();
        _fontBytes = fixture.FontBytes;
        _directFace = fixture.DirectFace;
        _directLookup = fixture.DirectLookup;
        _catalogRuntimeIndex = fixture.CatalogRuntimeIndex;
        _request = fixture.Request;
        _codepoints = fixture.Codepoints;
        _directResolver = () => _directLookup[_request.FontName];
        _catalogResolver = () => _catalogRuntimeIndex.Find(_request) ?? throw new InvalidOperationException("Catalog runtime index lost its face.");
        fixture.Validate();
    }

    /// <summary>Measures one common real native subset operation on the deterministic valid font.</summary>
    /// <returns>The nonempty subset byte count.</returns>
    [Benchmark]
    public int NativeSubset() => Subset(_directFace).Length;

    /// <summary>Measures one preloaded direct dictionary resolution.</summary>
    /// <returns>An observable resolved face wrapper.</returns>
    [Benchmark]
    public ResolverMeasurement DirectResolver() => new(_directResolver());

    /// <summary>Measures one loaded runtime-index catalog resolution.</summary>
    /// <returns>An observable resolved face wrapper.</returns>
    [Benchmark]
    public ResolverMeasurement CatalogResolver() => new(_catalogResolver());

    private byte[] Subset(RuntimeFontFace face)
    {
        byte[]? result = HarfBuzzSubsetNative.SubsetFont(_fontBytes, checked((uint)face.FaceIndex), _codepoints, NullLogger.Instance);
        return result is { Length: > 0 } ? result : throw new InvalidOperationException("Production native subsetter returned no data.");
    }
}

/// <summary>Observes a resolved face while making resolver benchmark allocation explicit.</summary>
public sealed class ResolverMeasurement
{
    /// <summary>Initializes a resolver measurement result.</summary>
    /// <param name="face">The resolved runtime face.</param>
    public ResolverMeasurement(RuntimeFontFace face) => Face = face;

    /// <summary>Gets the resolved runtime face.</summary>
    public RuntimeFontFace Face { get; }
}

internal static class SubsettingBenchmarkContract
{
    internal static object Create()
    {
        FontCandidateGenerationResult candidates = FontCandidateGenerator.Generate(SyntheticInventory.CreateEvidence());
        SubsettingFixture fixture = SubsettingFixture.Create();
        fixture.Validate();
        return new
        {
            fileCount = SyntheticInventory.FileCount,
            bucketPairCount = candidates.BucketPairCount,
            candidatePairCount = candidates.BucketPairCount,
            deepComparisonCount = candidates.DeepComparisonCount,
            allPairsCount = ((long)SyntheticInventory.FileCount * (SyntheticInventory.FileCount - 1)) / 2,
            nativeSubsetValidated = true
        };
    }
}

internal sealed class SubsettingFixture
{
    private SubsettingFixture(
        byte[] fontBytes,
        RuntimeFontFace directFace,
        RuntimeFontIndex catalogRuntimeIndex,
        FontDescriptor request,
        uint[] codepoints)
    {
        FontBytes = fontBytes;
        DirectFace = directFace;
        DirectLookup = new Dictionary<string, RuntimeFontFace>(StringComparer.Ordinal)
        {
            [directFace.FamilyName] = directFace
        };
        CatalogRuntimeIndex = catalogRuntimeIndex;
        Request = request;
        Codepoints = codepoints;
    }

    internal byte[] FontBytes { get; }
    internal RuntimeFontFace DirectFace { get; }
    internal Dictionary<string, RuntimeFontFace> DirectLookup { get; }
    internal RuntimeFontIndex CatalogRuntimeIndex { get; }
    internal FontDescriptor Request { get; }
    internal uint[] Codepoints { get; }

    internal static SubsettingFixture Create()
    {
        RuntimeFontFace directFace = SyntheticInventory.CreateFaces()[12345];
        return new(
            DeterministicSubsetFont.Create(),
            directFace,
            new RuntimeFontIndex([directFace]),
            new FontDescriptor(directFace.FamilyName, null, false, false),
            [32, 33, 34, 35]);
    }

    internal void Validate()
    {
        RuntimeFontFace directFace = DirectLookup[Request.FontName];
        RuntimeFontFace catalogFace = CatalogRuntimeIndex.Find(Request)
            ?? throw new InvalidOperationException("Catalog runtime index lost its face.");
        if (!ReferenceEquals(DirectFace, directFace) || !ReferenceEquals(directFace, catalogFace))
        {
            throw new InvalidOperationException("Direct and catalog resolution did not return the same published face.");
        }

        byte[] directProof = Subset(DirectFace);
        byte[] catalogProof = Subset(catalogFace);
        if (directProof.Length == 0 || !directProof.AsSpan().SequenceEqual(catalogProof))
        {
            throw new InvalidOperationException("The deterministic generated font did not produce identical nonempty HarfBuzzSubsetNative output for both resolution paths.");
        }
    }

    private byte[] Subset(RuntimeFontFace face)
    {
        byte[]? result = HarfBuzzSubsetNative.SubsetFont(FontBytes, checked((uint)face.FaceIndex), Codepoints, NullLogger.Instance);
        return result is { Length: > 0 } ? result : throw new InvalidOperationException("Production native subsetter returned no data.");
    }
}

/// <summary>Builds a deterministic in-memory SFNT fixture; it never accesses machine font directories.</summary>
internal static class DeterministicSubsetFont
{
    internal const int GlyphCount = 5;

    internal static byte[] Create()
    {
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = Head(),
            ["name"] = Name(),
            ["cmap"] = Cmap(),
            ["maxp"] = Maxp(),
            ["hhea"] = Hhea(),
            ["hmtx"] = Hmtx(),
            ["loca"] = Loca(),
            ["glyf"] = Glyf(),
            ["OS/2"] = Os2(),
            ["post"] = Post(),
        };
        int directoryLength = 12 + (tables.Count * 16);
        int offset = Align4(directoryLength);
        foreach (byte[] table in tables.Values) { offset = Align4(offset + table.Length); }
        byte[] font = new byte[offset];
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(0, 4), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(4, 2), (ushort)tables.Count);
        int maxPowerOfTwo = 1;
        while ((maxPowerOfTwo * 2) <= tables.Count) { maxPowerOfTwo *= 2; }
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(6, 2), (ushort)(maxPowerOfTwo * 16));
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(8, 2), (ushort)Math.Log2(maxPowerOfTwo));
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(10, 2), (ushort)((tables.Count * 16) - (maxPowerOfTwo * 16)));
        int recordOffset = 12;
        int tableOffset = Align4(directoryLength);
        foreach ((string tag, byte[] data) in tables)
        {
            Encoding.ASCII.GetBytes(tag, font.AsSpan(recordOffset, 4));
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(recordOffset + 4, 4), Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(recordOffset + 8, 4), (uint)tableOffset);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(recordOffset + 12, 4), (uint)data.Length);
            data.CopyTo(font, tableOffset);
            recordOffset += 16;
            tableOffset = Align4(tableOffset + data.Length);
        }

        int headOffset = BinaryPrimitives.ReadInt32BigEndian(font.AsSpan(12 + (Array.IndexOf(tables.Keys.ToArray(), "head") * 16) + 8, 4));
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(headOffset + 8, 4), unchecked(0xB1B0AFBAU - Checksum(font)));

        return font;
    }

    private static byte[] Head()
    {
        byte[] table = new byte[54];
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(0, 4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(4, 4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(12, 4), 0x5F0F3CF5);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(18, 2), 1000);
        BinaryPrimitives.WriteInt16BigEndian(table.AsSpan(50, 2), 1);
        return table;
    }

    private static byte[] Name()
    {
        byte[] text = Encoding.BigEndianUnicode.GetBytes("Deterministic Subset Fixture");
        byte[] table = new byte[18 + text.Length];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4, 2), 18);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(8, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(10, 2), 0x0409);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(14, 2), (ushort)text.Length);
        text.CopyTo(table, 18);
        return table;
    }

    private static byte[] Cmap()
    {
        byte[] table = new byte[40];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6, 2), 10);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8, 4), 12);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(12, 2), 12);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(16, 4), 28);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(24, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(28, 4), 32);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(32, 4), (uint)(GlyphCount + 30));
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(36, 4), 1);
        return table;
    }

    private static byte[] Maxp() { byte[] table = new byte[32]; BinaryPrimitives.WriteUInt32BigEndian(table, 0x00010000); BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4, 2), GlyphCount); return table; }
    private static byte[] Hhea() { byte[] table = new byte[36]; BinaryPrimitives.WriteUInt32BigEndian(table, 0x00010000); BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(34, 2), GlyphCount); return table; }
    private static byte[] Hmtx() { byte[] table = new byte[GlyphCount * 4]; for (int i = 0; i < GlyphCount; i++) { BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(i * 4, 2), 500); } return table; }
    private static byte[] Loca() { byte[] table = new byte[(GlyphCount + 1) * 4]; for (int i = 0; i <= GlyphCount; i++) { BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(i * 4, 4), (uint)(i * 10)); } return table; }
    private static byte[] Glyf() => new byte[GlyphCount * 10];
    private static byte[] Os2() { byte[] table = new byte[96]; BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4, 2), 400); BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6, 2), 5); BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(64, 2), 32); BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(66, 2), (ushort)(GlyphCount + 30)); return table; }
    private static byte[] Post() { byte[] table = new byte[32]; BinaryPrimitives.WriteUInt32BigEndian(table, 0x00030000); return table; }
    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (int offset = 0; offset < data.Length; offset += 4)
        {
            uint word = 0;
            for (int index = 0; index < 4 && offset + index < data.Length; index++) { word |= (uint)data[offset + index] << (24 - (index * 8)); }
            sum = unchecked(sum + word);
        }

        return sum;
    }

    private static int Align4(int value) => (value + 3) & ~3;
}
