using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>Schedules missing candidates on startup and naming configuration notifications.</summary>
public sealed class FontRenameCandidateRefreshService : BackgroundService
{
    private readonly FontRenameCandidateRefreshQueue _queue;
    private readonly IFontOperationRequestDispatcher _dispatcher;
    private readonly Func<Action, IDisposable> _subscribe;
    private readonly Func<FontNamingRules> _rules;
    private readonly ILogger<FontRenameCandidateRefreshService> _logger;
    private readonly SemaphoreSlim _signal = new(1, 1);
    private IDisposable? _subscription;

    /// <summary>Initializes a new instance of the <see cref="FontRenameCandidateRefreshService"/> class.</summary>
    /// <param name="queue">Durable candidate work queue.</param>
    /// <param name="dispatcher">Existing shared font task dispatcher.</param>
    /// <param name="subscribe">Configuration signal subscription.</param>
    /// <param name="rules">Current exact naming rules.</param>
    /// <param name="logger">Observable scheduling failures.</param>
    public FontRenameCandidateRefreshService(FontRenameCandidateRefreshQueue queue, IFontOperationRequestDispatcher dispatcher, Func<Action, IDisposable> subscribe, Func<FontNamingRules> rules, ILogger<FontRenameCandidateRefreshService> logger)
    {
        _queue = queue;
        _dispatcher = dispatcher;
        _subscribe = subscribe;
        _rules = rules;
        _logger = logger;
    }

    /// <summary>Signals a configuration or publication change without computing on the caller.</summary>
    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (Exception exception) when (exception is SemaphoreFullException or ObjectDisposedException)
        {
            // A signal is already pending, or the service has exited.
        }
    }

    /// <inheritdoc/>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _subscribe(Signal);
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return base.StopAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        base.Dispose();
        _signal.Dispose();
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool retry = false;
        bool recovered = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (retry)
                {
                    await _signal.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    await _signal.WaitAsync(stoppingToken).ConfigureAwait(false);
                }

                retry = false;
                if (!recovered)
                {
                    await _queue.RecoverInterruptedAsync(stoppingToken).ConfigureAwait(false);
                    recovered = true;
                }

                var fingerprint = _rules().GetFingerprint();
                var request = await _queue.EnsureCurrentAsync(stoppingToken).ConfigureAwait(false);
                if (request is not null)
                {
                    _logger.LogDebug("Accepted rename candidate refresh {RequestId} for naming rules {Fingerprint}.", request.Id, fingerprint);
                    _dispatcher.Signal();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                retry = true;
                _logger.LogError(exception, "Failed to schedule rename candidate refresh.");
            }
        }
    }
}
