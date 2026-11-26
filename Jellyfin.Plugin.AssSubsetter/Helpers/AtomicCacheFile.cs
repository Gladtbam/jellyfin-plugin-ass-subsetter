using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Helpers;

internal static class AtomicCacheFile
{
    internal static async Task<bool> WriteAsync(
        string outputPath,
        Func<string, CancellationToken, Task<bool>> writer,
        ILogger logger,
        CancellationToken cancellationToken,
        Action<string, string, CancellationToken>? publish = null)
    {
        string fullOutputPath = Path.GetFullPath(outputPath);
        string outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new InvalidOperationException("Cache output path has no parent directory.");
        Directory.CreateDirectory(outputDirectory);

        string extension = Path.GetExtension(fullOutputPath);
        string partialPath = Path.Join(
            outputDirectory,
            $"{Path.GetFileNameWithoutExtension(fullOutputPath)}.{Guid.NewGuid():N}.partial{extension}");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool success = await writer(partialPath, cancellationToken).ConfigureAwait(false);
            if (!success || !File.Exists(partialPath))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (publish is null)
            {
                File.Move(partialPath, fullOutputPath, true);
            }
            else
            {
                publish(partialPath, fullOutputPath, cancellationToken);
            }

            return true;
        }
        finally
        {
            try
            {
                File.Delete(partialPath);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "[AssSubsetter] Failed to remove partial cache file {PartialPath}.", partialPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "[AssSubsetter] Failed to remove partial cache file {PartialPath}.", partialPath);
            }
        }
    }
}
