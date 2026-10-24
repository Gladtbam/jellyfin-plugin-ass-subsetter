using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>Owns a bounded queue and fixed workers for deduplicated background jobs.</summary>
internal sealed class BoundedBackgroundScheduler : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly Channel<Job> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private readonly ILogger _logger;
    private Task? _stopTask;

    internal BoundedBackgroundScheduler(ILogger logger, int concurrency = 2, int pendingCapacity = 32)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pendingCapacity, 1);
        _logger = logger;
        _queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(pendingCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _workers = new Task[concurrency];
        for (int index = 0; index < concurrency; index++)
        {
            _workers[index] = Task.Run(WorkAsync);
        }
    }

    /// <summary>Gets the number of accepted jobs, including queued jobs.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _keys.Count;
            }
        }
    }

    /// <summary>Returns true for accepted or already tracked keys; false when full or stopping.</summary>
    /// <param name="key">Identity deduplicated across queued and running jobs.</param>
    /// <param name="execute">Work receiving the scheduler shutdown token.</param>
    /// <returns>True if accepted or already tracked; false if full or stopping.</returns>
    internal bool TryEnqueue(string key, Func<CancellationToken, Task> execute)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(execute);
        lock (_gate)
        {
            if (_stopTask is not null)
            {
                return false;
            }

            if (_keys.Contains(key))
            {
                return true;
            }

            _keys.Add(key);
            if (_queue.Writer.TryWrite(new Job(key, execute)))
            {
                return true;
            }

            _keys.Remove(key);
            return false;
        }
    }

    /// <summary>Cancels work and joins every worker. Repeated calls share the same completion.</summary>
    /// <returns>A task completing after all workers and cleanup finish.</returns>
    internal Task StopAsync()
    {
        lock (_gate)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task StopCoreAsync()
    {
        // Publish _stopTask before cancellation callbacks or worker completion can re-enter.
        await Task.Yield();
        _queue.Writer.TryComplete();
        try
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
            while (_queue.Reader.TryRead(out _))
            {
            }

            lock (_gate)
            {
                _keys.Clear();
            }

            _shutdown.Dispose();
        }
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (Job job in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    _shutdown.Token.ThrowIfCancellationRequested();
                    await job.Execute(_shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Background job {Key} failed.", job.Key);
                }
                finally
                {
                    lock (_gate)
                    {
                        _keys.Remove(job.Key);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private sealed record Job(string Key, Func<CancellationToken, Task> Execute);
}
