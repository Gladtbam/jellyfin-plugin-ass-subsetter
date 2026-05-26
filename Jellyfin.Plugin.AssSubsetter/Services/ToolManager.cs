using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
/// Tool manager for handling embedded mkvtool binaries.
/// </summary>
public class ToolManager : IDisposable
{
    private static readonly HttpClient _httpClient = new();
    private readonly ILogger<ToolManager> _logger;
    private readonly string _dataPath;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private string? _binaryPath;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolManager"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public ToolManager(ILogger<ToolManager> logger)
    {
        _logger = logger;
        _dataPath = Plugin.Instance!.PluginDataPath;
    }

    /// <summary>
    /// Gets the absolute path to the executable mkvtool binary, downloading it if necessary.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The path to the binary.</returns>
    public virtual async Task<string> GetToolPathAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(_binaryPath) && File.Exists(_binaryPath))
        {
            return _binaryPath;
        }

        await _downloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!string.IsNullOrEmpty(_binaryPath) && File.Exists(_binaryPath))
            {
                return _binaryPath;
            }

            string osName = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "osx";
            string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
            string extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

            string binaryName = $"mkvtool-{osName}-{arch}{extension}";
            _binaryPath = Path.Combine(_dataPath, binaryName);

            if (!File.Exists(_binaryPath))
            {
                _logger.LogInformation("mkvtool binary not found locally. Initiating download for {OS}-{Arch}...", osName, arch);

                string downloadUrl = $"https://github.com/MkvAutoSubset/MkvAutoSubset/releases/latest/download/{binaryName}";
                await DownloadToolAsync(downloadUrl, _binaryPath, cancellationToken).ConfigureAwait(false);

                if (osName != "windows")
                {
                    SetExecutablePermission(_binaryPath);
                }
            }

            return _binaryPath;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private void SetExecutablePermission(string filePath)
    {
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(
                    filePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

                _logger.LogInformation("Successfully granted execute permission to {Path} natively.", filePath);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception occurred while setting Unix file mode for mkvtool");
        }
    }

    private async Task DownloadToolAsync(string url, string outputPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string tempPath = outputPath + ".tmp";

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, outputPath, true);
        _logger.LogInformation("mkvtool download completed and saved to {Path}.", outputPath);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases unmanaged and - optionally - managed resources.
    /// </summary>
    /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            // 释放引发警告的内部 SemaphoreSlim 资源
            _downloadLock.Dispose();
        }

        _disposed = true;
    }
}
