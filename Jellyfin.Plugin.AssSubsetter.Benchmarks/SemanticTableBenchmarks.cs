using BenchmarkDotNet.Attributes;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Benchmarks;

/// <summary>Measures fingerprinting of unchanged layout table payloads.</summary>
[MemoryDiagnoser]
public class SemanticTableBenchmarks
{
    private OpenTypeFace _face = null!;

    /// <summary>Gets or sets the synthetic layout table size.</summary>
    [Params(4096, 1048576)]
    public int TableBytes { get; set; }

    /// <summary>Creates a table outside the measured operation.</summary>
    [GlobalSetup]
    public void Setup()
    {
        uint tag = OpenTypeTag.FromString("GPOS");
        byte[] data = new byte[TableBytes];
        new Random(42).NextBytes(data);
        _face = new OpenTypeFace(0, 0, 0x10000, new Dictionary<uint, OpenTypeTable>
        {
            [tag] = new OpenTypeTable(tag, 0, 0, data.Length, data)
        });
    }

    /// <summary>Builds strict and semantic fingerprints through the production pipeline.</summary>
    /// <returns>The computed fingerprints.</returns>
    [Benchmark]
    public FontFaceFingerprints BuildFace() => FontFingerprintBuilder.BuildFace(_face);
}
