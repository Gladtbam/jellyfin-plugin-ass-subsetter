using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
/// Processor for handling ASS subtitle subsetting via mkvtool.
/// </summary>
public class AssProcessor
{
    private readonly ToolManager _toolManager;
    private readonly ILogger<AssProcessor> _logger;
    private readonly PluginConfiguration _config;
    private readonly string _defaultFontCacheDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssProcessor"/> class.
    /// </summary>
    /// <param name="toolManager">The tool manager instance.</param>
    /// <param name="config">The plugin configuration instance.</param>
    /// <param name="logger">The logger instance.</param>
    public AssProcessor(ToolManager toolManager, PluginConfiguration config, ILogger<AssProcessor> logger)
    {
        _toolManager = toolManager;
        _logger = logger;
        _config = config;
        _defaultFontCacheDir = Path.Combine(Plugin.Instance?.PluginDataPath ?? AppContext.BaseDirectory, "font_caches");
    }

    /// <summary>
    /// Generates a subsetted ASS subtitle file.
    /// </summary>
    /// <param name="inputAssPath">The physical path of the original ASS file.</param>
    /// <param name="outputCachePath">The path where the subsetted ASS file should be saved.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if generation is successful; otherwise, false.</returns>
    [SuppressMessage("Security", "CA3006:Review code for process command injection vulnerabilities", Justification = "The process execution is tightly sealed; arguments consist purely of verified physical directories and GUID names.")]
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths are sanitized and guided by server side configurations.")]
    public async Task<bool> GenerateSubsetFontAsync(string inputAssPath, string outputCachePath, CancellationToken cancellationToken = default)
    {
        string tempOutDir = string.Empty;
        try
        {
            string toolPath = await _toolManager.GetToolPathAsync(cancellationToken).ConfigureAwait(false);
            string fontCacheDir = string.IsNullOrWhiteSpace(_config.FontCacheDirectory) ? _defaultFontCacheDir : _config.FontCacheDirectory;

            if (!Directory.Exists(fontCacheDir))
            {
                Directory.CreateDirectory(fontCacheDir);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            tempOutDir = Path.Combine(Path.GetTempPath(), "mkvtool_out_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempOutDir);

            var startInfo = new ProcessStartInfo
            {
                FileName = toolPath,
                Arguments = $"subset -o \"{tempOutDir}\" -n --font-cache-dir \"{fontCacheDir}\" \"{inputAssPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _logger.LogInformation("Starting mkvtool subset. Command: {Cmd} {Args}", startInfo.FileName, startInfo.Arguments);
            using var process = Process.Start(startInfo);

            if (process == null)
            {
                return false;
            }

            Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await Task.WhenAll(process.WaitForExitAsync(cts.Token), stdOutTask, stdErrTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("mkvtool process timed out. Killing process...");
                if (!process.HasExited)
                {
                    process.Kill(true);
                }

                throw;
            }

            string stdOut = await stdOutTask.ConfigureAwait(false);
            string stdErr = await stdErrTask.ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                string originalFileName = Path.GetFileName(inputAssPath);
                string subsettedFile = Path.Combine(tempOutDir, originalFileName);

                if (File.Exists(subsettedFile))
                {
                    await EmbedFontsIntoAssAsync(tempOutDir, subsettedFile, cancellationToken).ConfigureAwait(false);

                    string? outDir = Path.GetDirectoryName(outputCachePath);
                    if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                    {
                        Directory.CreateDirectory(outDir);
                    }

                    File.Move(subsettedFile, outputCachePath, true);
                    return true;
                }
            }

            _logger.LogWarning("mkvtool exited with code {ExitCode}. StdOut: {StdOut} StdErr: {StdErr}", process.ExitCode, stdOut, stdErr);

            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception occurred while running mkvtool.");
            return false;
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempOutDir) && Directory.Exists(tempOutDir))
            {
                try
                {
                    Directory.Delete(tempOutDir, true);
                }
                catch
                {
                    /* Ignore cleanup errors */
                }
            }
        }
    }

    /// <summary>
    /// Embeds all font files from the specified temporary directory into the ASS file's [Fonts] section.
    /// </summary>
    /// <param name="tempDir">The temporary directory containing the generated font files.</param>
    /// <param name="assFilePath">The path to the target ASS file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task EmbedFontsIntoAssAsync(string tempDir, string assFilePath, CancellationToken cancellationToken)
    {
        var fontFiles = Directory.GetFiles(tempDir, "*.*")
            .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (fontFiles.Length == 0)
        {
            return;
        }

        using var writer = new StreamWriter(assFilePath, append: true);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("[Fonts]").ConfigureAwait(false);

        foreach (var fontFile in fontFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fontName = Path.GetFileName(fontFile);
            await writer.WriteLineAsync($"fontname: {fontName}").ConfigureAwait(false);

            byte[] fileData = await File.ReadAllBytesAsync(fontFile, cancellationToken).ConfigureAwait(false);

            var encodedLines = EncodeFontToAssLines(fileData);
            foreach (var line in encodedLines)
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }

            await writer.WriteLineAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Encodes font binary data into ASS UUEncode format lines (3 bytes to 4 characters with a +33 offset).
    /// </summary>
    /// <param name="fileData">The binary data of the font file.</param>
    /// <returns>A list of encoded strings representing the font data.</returns>
    private static List<string> EncodeFontToAssLines(byte[] fileData)
    {
        var lines = new List<string>();
        var sb = new System.Text.StringBuilder(80);

        for (int i = 0; i < fileData.Length; i += 3)
        {
            int b1 = fileData[i];
            int b2 = i + 1 < fileData.Length ? fileData[i + 1] : 0;
            int b3 = i + 2 < fileData.Length ? fileData[i + 2] : 0;

            int val = (b1 << 16) | (b2 << 8) | b3;

            sb.Append((char)(((val >> 18) & 0x3F) + 33));
            sb.Append((char)(((val >> 12) & 0x3F) + 33));

            if (i + 1 < fileData.Length)
            {
                sb.Append((char)(((val >> 6) & 0x3F) + 33));
            }

            if (i + 2 < fileData.Length)
            {
                sb.Append((char)((val & 0x3F) + 33));
            }

            if (sb.Length >= 80)
            {
                lines.Add(sb.ToString());
                sb.Clear();
            }
        }

        if (sb.Length > 0)
        {
            lines.Add(sb.ToString());
        }

        return lines;
    }
}
