using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AssSubsetter.Helpers;

/// <summary>
///     Serializes asynchronous operations by key and removes idle key entries.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
internal sealed class AsyncKeyedLocker<TKey>
    where TKey : notnull
{
    private readonly Dictionary<TKey, Entry> _entries;
    private readonly object _gate = new();

    /// <summary>
    ///     Initializes a new instance of the <see cref="AsyncKeyedLocker{TKey}" /> class.
    /// </summary>
    /// <param name="comparer">The optional key comparer.</param>
    internal AsyncKeyedLocker(IEqualityComparer<TKey>? comparer = null)
    {
        _entries = new Dictionary<TKey, Entry>(comparer);
    }

    /// <summary>
    ///     Gets the number of keys that currently have holders or waiters.
    /// </summary>
    internal int ActiveKeyCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    ///     Acquires the lock for a key.
    /// </summary>
    /// <param name="key">The key to serialize on.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A lease that releases the key lock when disposed.</returns>
    internal async ValueTask<IDisposable> LockAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries.Add(key, entry);
            }

            entry.ReferenceCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, key, entry);
        }
        catch
        {
            ReleaseReference(key, entry);
            throw;
        }
    }

    private void Release(TKey key, Entry entry)
    {
        entry.Semaphore.Release();
        ReleaseReference(key, entry);
    }

    private void ReleaseReference(TKey key, Entry entry)
    {
        bool shouldDispose = false;
        lock (_gate)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0 &&
                _entries.TryGetValue(key, out Entry? current) &&
                ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
                shouldDispose = true;
            }
        }

        if (shouldDispose)
        {
            entry.Semaphore.Dispose();
        }
    }

    private sealed class Entry
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);

        internal int ReferenceCount { get; set; }
    }

    private sealed class Lease : IDisposable
    {
        private readonly Entry _entry;
        private readonly TKey _key;
        private AsyncKeyedLocker<TKey>? _owner;

        internal Lease(AsyncKeyedLocker<TKey> owner, TKey key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(_key, _entry);
        }
    }
}
