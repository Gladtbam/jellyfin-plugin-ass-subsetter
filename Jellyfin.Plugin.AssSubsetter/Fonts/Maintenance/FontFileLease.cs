using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>
/// Keeps a physical font file stable until its bytes have been copied into memory.
/// </summary>
public sealed class FontFileLease : IAsyncDisposable
{
    private Func<ValueTask>? _release;

    internal FontFileLease(string path, int faceIndex, Func<ValueTask> release)
    {
        Path = path;
        FaceIndex = faceIndex;
        _release = release;
    }

    /// <summary>Gets the stable physical font path.</summary>
    public string Path { get; }

    /// <summary>Gets the Face index within the leased container.</summary>
    public int FaceIndex { get; }

    /// <summary>Releases the read lease exactly once.</summary>
    /// <returns>A task representing release.</returns>
    public ValueTask DisposeAsync()
    {
        Func<ValueTask>? release = Interlocked.Exchange(ref _release, null);
        return release is null ? ValueTask.CompletedTask : release();
    }
}
