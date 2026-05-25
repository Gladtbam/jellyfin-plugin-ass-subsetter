using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
/// Tool manager for handling embedded mkvtool binaries.
/// </summary>
public class ToolManager
{
    private readonly ILogger<ToolManager> _logger;
    private readonly string _dataPath;
    private string? _binaryPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolManager"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public ToolManager(ILogger<ToolManager> logger)
    {
        _logger = logger;
        _dataPath = Plugin.Instance!.DataFolderPath;
    }

    /// <summary>
    /// Gets the absolute path to the executable mkvtool binary, extracting it if necessary.
    /// </summary>
    /// <returns>The path to the binary.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the embedded resource is missing.</exception>
    public virtual string GetToolPath()
    {
        if (!string.IsNullOrEmpty(_binaryPath) && File.Exists(_binaryPath))
        {
            return _binaryPath;
        }

        string osName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : "macos";
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
        string extension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty;

        string binaryName = $"mkvtool-{osName}-{arch}{extension}";
        _binaryPath = Path.Combine(_dataPath, binaryName);

        if (!File.Exists(_binaryPath))
        {
            _logger.LogInformation("Extracting mkvtool binary to: {Path}", _binaryPath);

            string resourceName = $"{GetType().Namespace}.Resources.{binaryName}";

            ExtractResource(resourceName, _binaryPath);

            if (osName != "windows")
            {
                SetExecutablePermission(_binaryPath);
            }
        }

        return _binaryPath;
    }

    private void SetExecutablePermission(string filePath)
    {
        try
        {
            // 明确告诉编译器：这行代码只在 Linux 或 macOS 下执行
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

    private void ExtractResource(string resourceName, string outputPath)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using Stream? resourceStream = assembly.GetManifestResourceStream(resourceName);
        if (resourceStream == null)
        {
            throw new FileNotFoundException($"Cannot find embedded resource: {resourceName}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using FileStream fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        resourceStream.CopyTo(fileStream);
    }
}
