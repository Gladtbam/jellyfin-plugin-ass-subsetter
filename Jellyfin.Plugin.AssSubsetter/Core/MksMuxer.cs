using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Native;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Core;

/// <summary>
///     Packages rewritten ASS content and subset fonts into a Matroska subtitle container.
/// </summary>
public class MksMuxer
{
    private const int MaximumLoggedStderrLength = 8192;
    private readonly Func<string?> _ffmpegPathFactory;
    private readonly ILogger<MksMuxer> _logger;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> _processRunner;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MksMuxer" /> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ffmpegPathFactory">Optional FFmpeg locator override for tests.</param>
    /// <param name="processRunner">Optional process runner override for tests.</param>
    public MksMuxer(
        ILogger<MksMuxer> logger,
        Func<string?>? ffmpegPathFactory = null,
        Func<ProcessStartInfo, CancellationToken, Task<int>>? processRunner = null)
    {
        _logger = logger;
        _ffmpegPathFactory = ffmpegPathFactory ?? FfmpegLocator.FindPath;
        _processRunner = processRunner ?? RunProcessAsync;
    }

    /// <summary>
    ///     Packages an ASS subset artifact into an MKS file.
    /// </summary>
    /// <param name="artifact">Rewritten ASS and font attachments.</param>
    /// <param name="outputPath">Final cache output path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true" /> when a non-empty MKS was atomically promoted.</returns>
    [SuppressMessage("Security", "CA3006:Review code for process command injection vulnerabilities", Justification = "The executable is Jellyfin's FFmpeg and every argument is passed separately through ArgumentList.")]
    public virtual async Task<bool> MuxAsync(SubsetArtifact artifact, string outputPath, CancellationToken cancellationToken = default)
    {
        string? ffmpegPath = _ffmpegPathFactory();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            _logger.LogWarning("[AssSubsetter] Jellyfin FFmpeg could not be located; MKS generation is unavailable.");
            return false;
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        string outputDirectory = Path.GetDirectoryName(fullOutputPath) ?? throw new InvalidOperationException("MKS output path has no parent directory.");
        Directory.CreateDirectory(outputDirectory);

        string operationId = Guid.NewGuid().ToString("N");
        string workspacePath = Path.Join(outputDirectory, $".mks-{operationId}");
        string partialPath = Path.Join(outputDirectory, $"{Path.GetFileName(fullOutputPath)}.{operationId}.partial.mks");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(workspacePath);

            string assPath = Path.Join(workspacePath, "subtitle.ass");
            await File.WriteAllTextAsync(assPath, artifact.AssContent, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            var fontPaths = new List<string>(artifact.Fonts.Count);
            foreach (SubsetFontAttachment font in artifact.Fonts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string safeName = Path.GetFileName(font.FileName);
                if (!string.Equals(safeName, font.FileName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(safeName))
                {
                    _logger.LogWarning("[AssSubsetter] Rejected unsafe MKS attachment name: {FileName}", font.FileName);
                    return false;
                }

                string fontPath = Path.Join(workspacePath, safeName);
                await File.WriteAllBytesAsync(fontPath, font.Data, cancellationToken).ConfigureAwait(false);
                fontPaths.Add(fontPath);
            }

            IReadOnlyList<string> arguments = BuildArguments(assPath, fontPaths, artifact.Fonts, partialPath);
            var startInfo = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                CreateNoWindow = true
            };

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            int exitCode = await _processRunner(startInfo, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                _logger.LogWarning("[AssSubsetter] FFmpeg MKS muxing exited with code {ExitCode}.", exitCode);
                return false;
            }

            if (!File.Exists(partialPath) || new FileInfo(partialPath).Length == 0)
            {
                _logger.LogWarning("[AssSubsetter] FFmpeg did not produce a non-empty MKS output.");
                return false;
            }

            File.Move(partialPath, fullOutputPath, true);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[AssSubsetter] MKS generation was cancelled.");
            return false;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "[AssSubsetter] IO error while generating MKS.");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "[AssSubsetter] Permission error while generating MKS.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AssSubsetter] Unexpected error while generating MKS.");
            return false;
        }
        finally
        {
            TryDeleteFile(partialPath);
            TryDeleteDirectory(workspacePath);
        }
    }

    /// <summary>
    ///     Builds the FFmpeg argument vector for MKS muxing.
    /// </summary>
    /// <param name="assPath">Temporary ASS input path.</param>
    /// <param name="fontPaths">Temporary font input paths.</param>
    /// <param name="fonts">Font attachment metadata.</param>
    /// <param name="outputPath">Temporary Matroska output path.</param>
    /// <returns>Ordered FFmpeg arguments.</returns>
    internal static IReadOnlyList<string> BuildArguments(
        string assPath,
        IReadOnlyList<string> fontPaths,
        IReadOnlyList<SubsetFontAttachment> fonts,
        string outputPath)
    {
        if (fontPaths.Count != fonts.Count)
        {
            throw new ArgumentException("Font paths and metadata counts must match.", nameof(fontPaths));
        }

        var arguments = new List<string>
        {
            "-nostdin",
            "-y",
            "-i",
            assPath,
            "-map",
            "0:s:0",
            "-c:s",
            "copy"
        };

        for (int index = 0; index < fonts.Count; index++)
        {
            arguments.Add("-attach");
            arguments.Add(fontPaths[index]);
            arguments.Add($"-metadata:s:t:{index}");
            arguments.Add($"filename={fonts[index].FileName}");
            arguments.Add($"-metadata:s:t:{index}");
            arguments.Add($"mimetype={fonts[index].MimeType}");
        }

        arguments.Add("-f");
        arguments.Add("matroska");
        arguments.Add(outputPath);
        return arguments;
    }

    private async Task<int> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Jellyfin FFmpeg.");
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            throw;
        }

        string stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0 && stderr.Length > 0)
        {
            string boundedStderr = stderr.Length <= MaximumLoggedStderrLength ? stderr : stderr.Substring(0, MaximumLoggedStderrLength);
            _logger.LogWarning("[AssSubsetter] FFmpeg stderr: {Stderr}", boundedStderr);
        }

        return process.ExitCode;
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "[AssSubsetter] Could not delete temporary MKS file {Path}.", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "[AssSubsetter] Could not delete temporary MKS file {Path}.", path);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "[AssSubsetter] Could not delete temporary MKS workspace {Path}.", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "[AssSubsetter] Could not delete temporary MKS workspace {Path}.", path);
        }
    }
}
