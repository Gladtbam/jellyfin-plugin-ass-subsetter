using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Benchmarks;

/// <summary>Measures semantic serialization of dense Unicode maps.</summary>
[MemoryDiagnoser]
public class MappingFingerprintBenchmarks
{
    private OpenTypeFace _face = null!;

    /// <summary>Gets or sets the number of Unicode mappings.</summary>
    [Params(128, 20000)]
    public int Mappings { get; set; }

    /// <summary>Creates a valid identity and format 12 map outside measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        byte[] name = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(4), 18);
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(6), 3);
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16BigEndian(name.AsSpan(14), 2);
        name[19] = (byte)'A';
        byte[] cmap = new byte[40];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), 10);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8), 12);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12), 12);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(16), 28);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(32), (uint)(Mappings - 1));
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(36), 1);
        var tables = new Dictionary<uint, OpenTypeTable>();
        foreach (var entry in new[] { (Tag: "name", Data: name), (Tag: "cmap", Data: cmap) })
        {
            uint tag = OpenTypeTag.FromString(entry.Tag);
            tables.Add(tag, new OpenTypeTable(tag, 0, 0, entry.Data.Length, entry.Data));
        }

        _face = new OpenTypeFace(0, 0, 0x10000, tables);
        if (!FontFingerprintBuilder.BuildFace(_face).IsSemanticEvidenceComplete)
        {
            throw new InvalidOperationException("Benchmark requires complete semantic mappings.");
        }
    }

    /// <summary>Builds strict and semantic fingerprints including mapping serialization.</summary>
    /// <returns>The computed fingerprints.</returns>
    [Benchmark]
    public FontFaceFingerprints BuildFace() => FontFingerprintBuilder.BuildFace(_face);
}
