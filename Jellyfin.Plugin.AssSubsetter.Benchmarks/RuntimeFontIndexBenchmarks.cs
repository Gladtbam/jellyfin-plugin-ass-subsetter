using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Models;

namespace Jellyfin.Plugin.AssSubsetter.Benchmarks;

/// <summary>Measures deterministic 20,000-file runtime lookup construction and resolution.</summary>
[MemoryDiagnoser]
[MedianColumn]
public class RuntimeFontIndexBenchmarks
{
    private RuntimeFontIndex _index = null!;
    private RuntimeFontFace[] _faces = null!;
    private FontDescriptor _exact = null!;
    private FontDescriptor _loose = null!;

    /// <summary>Builds the deterministic fixture outside timed methods.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _faces = SyntheticInventory.CreateFaces();
        _index = new RuntimeFontIndex(_faces);
        _exact = new FontDescriptor("Synthetic Family 12345", null, false, false);
        _loose = new FontDescriptor("Family 12345", null, false, false);
    }

    /// <summary>Resolves an exact family through an already-built immutable runtime index.</summary>
    /// <returns>The selected face.</returns>
    [Benchmark]
    public RuntimeFontFace? ExactLookup() => _index.Find(_exact);

    /// <summary>Resolves a bounded loose family query through the same runtime index.</summary>
    /// <returns>The selected face.</returns>
    [Benchmark]
    public RuntimeFontFace? LooseLookup() => _index.Find(_loose);

    /// <summary>Constructs an immutable runtime index from exactly 20,000 generated faces.</summary>
    /// <returns>The constructed index.</returns>
    [Benchmark]
    public RuntimeFontIndex ConstructIndex() => new(_faces);
}
