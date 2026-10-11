using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>
/// Coordinates runtime readers with quarantine and rename writers per physical path.
/// </summary>
public sealed class FontMaintenanceCoordinator
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PathState> _states = new(FontPathComparison.Comparer);
    private readonly HashSet<string> _unavailablePaths = new(FontPathComparison.Comparer);
    private readonly HashSet<string> _renamedPaths = new(FontPathComparison.Comparer);
    private int _unavailableCount;

    /// <summary>Checks the memory-only maintenance fence without inspecting a file or waiting for a writer.</summary>
    /// <param name="path">The selected runtime file path.</param>
    /// <returns>True when maintenance has not invalidated the path.</returns>
    public bool IsPathAvailable(string path)
    {
        if (Volatile.Read(ref _unavailableCount) == 0)
        {
            return true;
        }

        string normalized = Path.GetFullPath(path);
        lock (_sync)
        {
            return !_unavailablePaths.Contains(normalized) && !_renamedPaths.Contains(normalized);
        }
    }

    /// <summary>Fences a path before mutation or reopens it after verified restoration while its exclusive lease is held.</summary>
    /// <param name="path">The exact affected path.</param>
    /// <param name="available">Whether verified bytes are safely selectable again.</param>
    public void SetPathAvailable(string path, bool available)
        => SetPathAvailable(_unavailablePaths, path, available);

    /// <summary>Fences a renamed path separately from quarantine until a complete runtime generation replaces it.</summary>
    /// <param name="path">The path still present in the previous runtime generation.</param>
    /// <param name="available">Whether a verified rollback makes that path selectable again.</param>
    public void SetRenamePathAvailable(string path, bool available)
        => SetPathAvailable(_renamedPaths, path, available);

    /// <summary>Releases rename-only fences after a complete analyzed runtime generation is published.</summary>
    public void ClearRenameFences()
    {
        lock (_sync)
        {
            _renamedPaths.Clear();
            Volatile.Write(ref _unavailableCount, _unavailablePaths.Count);
        }
    }

    private void SetPathAvailable(HashSet<string> unavailable, string path, bool available)
    {
        string normalized = Path.GetFullPath(path);
        lock (_sync)
        {
            if (available)
            {
                unavailable.Remove(normalized);
            }
            else
            {
                unavailable.Add(normalized);
            }

            Volatile.Write(ref _unavailableCount, _unavailablePaths.Count + _renamedPaths.Count);
        }
    }

    /// <summary>Checks whether runtime readers or maintenance currently hold a path.</summary>
    /// <param name="path">The exact path to check without waiting.</param>
    /// <returns>True when the file is in use.</returns>
    public bool IsInUse(string path)
    {
        PathState state = GetState(path);
        lock (_sync)
        {
            return state.ReaderCount != 0 || state.WriterGate.CurrentCount == 0;
        }
    }

    /// <summary>Checks active descendant file leases before removing an empty ancestor.</summary>
    /// <param name="directory">The physical directory boundary.</param>
    /// <returns>True when any reader or writer references the directory.</returns>
    public bool IsDirectoryInUse(string directory)
    {
        lock (_sync)
        {
            return _states.Any(pair => (pair.Value.ReaderCount != 0 || pair.Value.WriterGate.CurrentCount == 0) && FontPathComparison.IsWithin(pair.Key, directory));
        }
    }

    /// <summary>Acquires a shared read lease for a selected runtime Face.</summary>
    /// <param name="face">The runtime Face whose file must remain stable.</param>
    /// <param name="cancellationToken">A token that cancels while a writer is active.</param>
    /// <returns>A read lease that must be disposed after font bytes are copied.</returns>
    public async Task<FontFileLease> AcquireReadAsync(RuntimeFontFace face, CancellationToken cancellationToken)
    {
        PathState state = GetState(face.Path);
        await state.WriterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            state.ReaderCount++;
            state.WriterGate.Release();
        }

        return new FontFileLease(face.Path, face.FaceIndex, () => ReleaseReadAsync(state));
    }

    /// <summary>Acquires exclusive access after all active readers of every path have completed.</summary>
    /// <param name="paths">The exact physical paths that a maintenance operation will mutate.</param>
    /// <param name="cancellationToken">A token that cancels exclusive acquisition.</param>
    /// <returns>An exclusive lease that reopens the paths to readers when disposed.</returns>
    public async Task<IAsyncDisposable> AcquireExclusiveAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        PathState[] states = paths
            .Select(path => (Path: Path.GetFullPath(path), State: GetState(path)))
            .DistinctBy(item => item.Path, FontPathComparison.Comparer)
            .OrderBy(item => item.Path, FontPathComparison.Comparer)
            .Select(item => item.State)
            .ToArray();
        var acquired = new List<PathState>(states.Length);
        try
        {
            foreach (PathState state in states)
            {
                await state.WriterGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired.Add(state);
                Task readersDrained;
                lock (_sync)
                {
                    readersDrained = state.ReaderCount == 0 ? Task.CompletedTask : state.ReadersDrained.Task;
                }

                await readersDrained.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new ExclusiveLease(acquired);
        }
        catch
        {
            foreach (PathState state in acquired)
            {
                state.WriterGate.Release();
            }

            throw;
        }
    }

    private PathState GetState(string path)
    {
        string normalized = Path.GetFullPath(path);
        lock (_sync)
        {
            if (!_states.TryGetValue(normalized, out PathState? state))
            {
                state = new PathState();
                _states.Add(normalized, state);
            }

            return state;
        }
    }

    private ValueTask ReleaseReadAsync(PathState state)
    {
        lock (_sync)
        {
            state.ReaderCount--;
            if (state.ReaderCount == 0)
            {
                state.ReadersDrained.TrySetResult();
                state.ReadersDrained = NewSignal();
            }
        }

        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class PathState
    {
        public SemaphoreSlim WriterGate { get; } = new(1, 1);

        public int ReaderCount { get; set; }

        public TaskCompletionSource ReadersDrained { get; set; } = NewSignal();
    }

    private sealed class ExclusiveLease(IReadOnlyList<PathState> states) : IAsyncDisposable
    {
        private IReadOnlyList<PathState>? _states = states;

        public ValueTask DisposeAsync()
        {
            IReadOnlyList<PathState>? statesToRelease = Interlocked.Exchange(ref _states, null);
            if (statesToRelease is not null)
            {
                for (int index = statesToRelease.Count - 1; index >= 0; index--)
                {
                    statesToRelease[index].WriterGate.Release();
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
