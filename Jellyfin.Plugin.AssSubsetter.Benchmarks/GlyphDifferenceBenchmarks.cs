using BenchmarkDotNet.Attributes;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Benchmarks;

/// <summary>Measures classification of large fonts with small and extensive glyph differences.</summary>
[MemoryDiagnoser]
public class GlyphDifferenceBenchmarks
{
    private readonly FontRelationshipClassifier _classifier = new();
    private FontFaceEvidence _left = null!;
    private FontFaceEvidence _right = null!;

    /// <summary>Gets or sets the number of changed glyphs in a 20,000-character font.</summary>
    [Params(16, 20_000)]
    public int Differences { get; set; }

    /// <summary>Constructs complete evidence outside the timed operation.</summary>
    [GlobalSetup]
    public void Setup()
    {
        ReadOnlyMemory<byte> original = new byte[] { 1 };
        ReadOnlyMemory<byte> changed = new byte[] { 2 };
        FontFaceEvidence template = SyntheticInventory.CreateEvidence()[0];
        _left = template with
        {
            FileHash = original,
            StrictFingerprint = original,
            UnicodeCoverage = [new UnicodeRange(0, 19_999)],
            CharacterFingerprints = Enumerable.Range(0, 20_000).ToDictionary(index => (uint)index, _ => original),
            SemanticFingerprints = new Dictionary<FontSemanticGroup, ReadOnlyMemory<byte>> { [FontSemanticGroup.OutlinesAndMetrics] = original }
        };
        _right = _left with
        {
            PhysicalFileId = "changed-font",
            FileHash = changed,
            StrictFingerprint = changed,
            CharacterFingerprints = Enumerable.Range(0, 20_000).ToDictionary(index => (uint)index, index => index < Differences ? changed : original),
            SemanticFingerprints = new Dictionary<FontSemanticGroup, ReadOnlyMemory<byte>> { [FontSemanticGroup.OutlinesAndMetrics] = changed }
        };
        FontRelationshipKind expected = Differences == 16
            ? FontRelationshipKind.SmallGlyphDifference
            : FontRelationshipKind.SameIdentityContentConflict;
        if (_classifier.Classify(_left, _right).Kind != expected)
        {
            throw new InvalidOperationException("Benchmark input does not exercise the intended glyph-difference classification.");
        }
    }

    /// <summary>Classifies a complete pair through the production relationship pipeline.</summary>
    /// <returns>The classification and any bounded glyph differences.</returns>
    [Benchmark]
    public FontRelationshipResult Classify() => _classifier.Classify(_left, _right);
}
