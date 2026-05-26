using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
/// Background service to ensure mkvtool is downloaded asynchronously at plugin startup.
/// </summary>
public class ToolDownloadHostedService : IHostedService
{
    private readonly ToolManager _toolManager;
    private readonly ILogger<ToolDownloadHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolDownloadHostedService"/> class.
    /// </summary>
    /// <param name="toolManager">The tool manager instance.</param>
    /// <param name="logger">The logger instance.</param>
    public ToolDownloadHostedService(ToolManager toolManager, ILogger<ToolDownloadHostedService> logger)
    {
        _toolManager = toolManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Plugin loaded. Checking and downloading mkvtool in the background if necessary...");

        _ = Task.Run(
            async () =>
        {
            try
            {
                await _toolManager.GetToolPathAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download mkvtool during startup. Will retry on demand when a subtitle is requested.");
            }
        },
            cancellationToken);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
