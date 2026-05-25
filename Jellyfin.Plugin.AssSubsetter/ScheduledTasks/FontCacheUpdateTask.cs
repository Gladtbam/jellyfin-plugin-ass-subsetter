using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>
/// Scheduled task to scan physical font directories and build the mkvtool font cache index.
/// </summary>
public class FontCacheUpdateTask : IScheduledTask
{
    private static readonly char[] _splitChars = new[] { ';', ',' };

    private readonly ToolManager _toolManager;
    private readonly PluginConfiguration _config;
    private readonly ILogger<FontCacheUpdateTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FontCacheUpdateTask"/> class.
    /// </summary>
    /// <param name="toolManager">The tool manager.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="logger">The logger.</param>
    public FontCacheUpdateTask(
        ToolManager toolManager,
        PluginConfiguration config,
        ILogger<FontCacheUpdateTask> logger)
    {
        _toolManager = toolManager;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "构建 mkvtool 字体索引缓存";

    /// <inheritdoc />
    public string Key => "MkvtoolFontCacheUpdateTask";

    /// <inheritdoc />
    public string Description => "扫描系统的系统字体与配置的自定义字体目录，为 mkvtool 重建加速缓存。";

    /// <inheritdoc />
    public string Category => "Subtitles";

    private IEnumerable<string> GetFontDirectories()
    {
        var dirs = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            dirs.Add("/usr/share/fonts");
            dirs.Add("/usr/local/share/fonts");
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/fonts"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            dirs.Add("/System/Library/Fonts");
            dirs.Add("/Library/Fonts");
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Fonts"));
        }

        if (!string.IsNullOrWhiteSpace(_config.CustomFontDirectories))
        {
            var customDirs = _config.CustomFontDirectories.Split(_splitChars, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in customDirs)
            {
                dirs.Add(dir.Trim());
            }
        }

        return dirs.Where(Directory.Exists).Distinct();
    }

    /// <inheritdoc />
    [SuppressMessage("Security", "CA3006:Review code for process command injection vulnerabilities", Justification = "Controlled internal execution")]
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("开始执行 mkvtool 字体数据库更新任务...");
        progress.Report(0);

        string toolPath = _toolManager.GetToolPath();

        string fontCacheDir = string.IsNullOrWhiteSpace(_config.FontCacheDirectory)
            ? Path.Combine(Plugin.Instance?.DataFolderPath ?? AppContext.BaseDirectory, "font_caches")
            : _config.FontCacheDirectory;

        if (!Directory.Exists(fontCacheDir))
        {
            Directory.CreateDirectory(fontCacheDir);
        }
        else
        {
            _logger.LogInformation("正在清理旧的字体索引缓存文件...");
            try
            {
                var oldCaches = Directory.GetFiles(fontCacheDir, "*.cache");
                foreach (var file in oldCaches)
                {
                    File.Delete(file);
                }

                _logger.LogInformation("旧缓存清理完毕，共清理了 {Count} 个文件。", oldCaches.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "清理旧缓存时发生部分异常，但这不影响后续重构。");
            }
        }

        var fontDirs = GetFontDirectories().ToList();
        if (fontDirs.Count == 0)
        {
            _logger.LogWarning("未发现任何有效的字体目录，跳过缓存构建。");
            progress.Report(100);
            return;
        }

        int totalDirs = fontDirs.Count;
        int currentDir = 0;

        foreach (var fontDir in fontDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation("正在为目录构建字体缓存: {Dir}", fontDir);

            var startInfo = new ProcessStartInfo
            {
                FileName = toolPath,
                Arguments = $"cache \"{fontDir}\" --font-cache-dir \"{fontCacheDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);

            if (process == null)
            {
                _logger.LogWarning("无法启动 mkvtool 处理目录: {Dir}", fontDir);
                continue;
            }

            try
            {
                var errorReadTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    string error = await errorReadTask.ConfigureAwait(false);
                    _logger.LogWarning("处理目录 {Dir} 时异常退出，退出码 {Code}。错误: {Error}", fontDir, process.ExitCode, error);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("mkvtool 缓存任务被强制取消。");
                if (!process.HasExited)
                {
                    process.Kill(true);
                }

                throw;
            }

            currentDir++;
            double percent = ((double)currentDir / totalDirs) * 100;
            progress.Report(percent);
        }

        _logger.LogInformation("mkvtool 字体数据库缓存构建全部完成！");
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}
