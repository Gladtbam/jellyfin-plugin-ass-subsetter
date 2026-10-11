using System.Buffers.Binary;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

public sealed class UnicodeCoverageCodecTests
{
    [Fact]
    public void Decode_ShouldKeepLargeCoverageSnapshotWithinMemoryBudget()
    {
        const int rangeCount = 100_000;
        byte[] encoded = new byte[rangeCount * 8];
        for (int index = 0; index < rangeCount; index++)
        {
            uint scalar = (uint)(index * 2);
            BinaryPrimitives.WriteUInt32BigEndian(encoded.AsSpan(index * 8, 4), scalar);
            BinaryPrimitives.WriteUInt32BigEndian(encoded.AsSpan((index * 8) + 4, 4), scalar);
        }

        _ = UnicodeCoverageCodec.Decode([0, 0, 0, 1, 0, 0, 0, 1]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ranges = UnicodeCoverageCodec.Decode(encoded);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(rangeCount, ranges.Count);
        Assert.True(allocated < 2_000_000, $"Coverage decoding allocated {allocated:N0} bytes for {rangeCount:N0} ranges.");
    }
}
