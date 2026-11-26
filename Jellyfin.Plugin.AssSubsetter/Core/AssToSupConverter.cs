using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Native;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Core;

/// <summary>
///     Service that converts ASS subtitle files to SUP (PGS) format
///     using libass for rendering and a pure C# PGS encoder for output.
/// </summary>
public partial class AssToSupConverter : IDisposable
{
    private readonly Func<PluginConfiguration> _configFactory;
    private readonly ILogger<AssToSupConverter> _logger;
    private readonly AssProcessor? _assProcessor;
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AssToSupConverter" /> class.
    /// </summary>
    /// <param name="configFactory">The plugin configuration factory.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="assProcessor">Published, leased font artifact producer. Conversion fails closed when absent.</param>
    public AssToSupConverter(Func<PluginConfiguration> configFactory, ILogger<AssToSupConverter> logger, AssProcessor? assProcessor = null)
    {
        _configFactory = configFactory;
        _logger = logger;
        _assProcessor = assProcessor;
    }

    private PluginConfiguration Config => _configFactory();

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Converts an ASS subtitle file to SUP (PGS) format using libass rendering.
    /// </summary>
    /// <param name="inputAssPath">Path to the input ASS file.</param>
    /// <param name="outputSupPath">Path for the output SUP file.</param>
    /// <param name="videoWidth">Video frame width in pixels.</param>
    /// <param name="videoHeight">Video frame height in pixels.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if conversion succeeded.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Failed native rendering returns control to the original subtitle pipeline.")]
    public async Task<bool> ConvertAsync(
        string inputAssPath,
        string outputSupPath,
        int videoWidth,
        int videoHeight,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("[AssSubsetter] Starting ASS to SUP conversion for {File} ({Width}x{Height})...", inputAssPath, videoWidth, videoHeight);
            }

            SubsetArtifact? artifact = _assProcessor is null ? null :
                await _assProcessor.GenerateSubsetArtifactAsync(inputAssPath, cancellationToken).ConfigureAwait(false);
            if (artifact is null || artifact.HasMissingFonts || artifact.Fonts.Any(font => font.Data.Length == 0))
            {
                return false;
            }

            var events = ParseArtifactEvents(artifact.AssContent);
            if (events.Count == 0)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("[AssSubsetter] No dialogue events found in {File}. Skipping conversion.", inputAssPath);
                }

                return false;
            }

            bool result = await AtomicCacheFile.WriteAsync(
                outputSupPath,
                async (partialPath, token) =>
                {
                    // Run the CPU-intensive rendering on a thread pool thread
                    bool rendered = await Task.Run(
                        () => RenderAndEncode(artifact, partialPath, videoWidth, videoHeight, events, token),
                        token).ConfigureAwait(false);

                    return rendered &&
                           File.Exists(partialPath) &&
                           new FileInfo(partialPath).Length > 0;
                },
                _logger,
                cancellationToken).ConfigureAwait(false);

            if (result && _logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("[AssSubsetter] ASS to SUP conversion completed successfully: {Output}", outputSupPath);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[AssSubsetter] ASS to SUP conversion was cancelled for {File}.", inputAssPath);
            return false;
        }
        catch (DllNotFoundException ex)
        {
            _logger.LogError(ex, "[AssSubsetter] libass shared library not found. ASS to SUP conversion is not available on this system.");
            return false;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "[AssSubsetter] IO error during ASS to SUP conversion.");
            return false;
        }

        // codeql[cs/catch-of-all-exceptions] Justification: Native interop with libass can throw unpredictable exceptions.
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AssSubsetter] Unexpected error during ASS to SUP conversion.");
            return false;
        }
    }

    private unsafe bool RenderAndEncode(
        SubsetArtifact artifact,
        string outputSupPath,
        int videoWidth,
        int videoHeight,
        List<AssEvent> events,
        CancellationToken cancellationToken)
    {
        IntPtr library = IntPtr.Zero;
        IntPtr renderer = IntPtr.Zero;
        IntPtr track = IntPtr.Zero;

        try
        {
            // Initialize libass
            library = LibassNative.AssLibraryInit();
            if (library == IntPtr.Zero)
            {
                _logger.LogError("[AssSubsetter] Failed to initialize libass library.");
                return false;
            }

            // Consume only bytes prepared under the published font selection's leases.
            foreach (SubsetFontAttachment font in artifact.Fonts)
            {
                fixed (byte* data = font.Data)
                {
                    LibassNative.AssAddFont(library, font.FileName, data, font.Data.Length);
                }
            }

            // Initialize renderer
            renderer = LibassNative.AssRendererInit(library);
            if (renderer == IntPtr.Zero)
            {
                _logger.LogError("[AssSubsetter] Failed to initialize libass renderer.");
                return false;
            }

            // Configure renderer
            LibassNative.AssSetFrameSize(renderer, videoWidth, videoHeight);
            LibassNative.AssSetStorageSize(renderer, videoWidth, videoHeight);

            // Disable system font discovery; only prepared attachments are available.
            LibassNative.AssSetFonts(renderer, null, null, 0, null, 0);

            // Load ASS file
            byte[] assBytes = Encoding.UTF8.GetBytes(artifact.AssContent);
            fixed (byte* data = assBytes)
            {
                track = LibassNative.AssReadMemory(library, data, (nuint)assBytes.Length, null);
            }

            if (track == IntPtr.Zero)
            {
                _logger.LogError("[AssSubsetter] Failed to load prepared ASS artifact.");
                return false;
            }

            // Render and encode
            int frameIntervalMs = 1000 / Math.Clamp(Config.AssToSupFrameRate, 10, 60);

            using var fileStream = new FileStream(outputSupPath, FileMode.Create, FileAccess.Write, FileShare.None);

            ushort compositionNumber = 0;
            bool wasVisible = false;

            // Build sorted list of all interesting time points
            var timePoints = BuildTimePoints(events, frameIntervalMs);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("[AssSubsetter] Processing {Count} time points for rendering...", timePoints.Count);
            }

            foreach (long timeMs in timePoints)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Render frame at this timestamp
                IntPtr imagePtr = LibassNative.AssRenderFrame(renderer, track, timeMs, out int changed);

                if (changed == 0 && wasVisible)
                {
                    // No change - skip this frame
                    continue;
                }

                if (imagePtr == IntPtr.Zero)
                {
                    // No subtitle at this time
                    if (wasVisible)
                    {
                        // Write clear set
                        long pts90 = timeMs * 90; // Convert ms to 90kHz ticks
                        PgsEncoder.WriteClearSet(fileStream, pts90, videoWidth, videoHeight, compositionNumber);
                        compositionNumber++;
                        wasVisible = false;
                    }

                    continue;
                }

                byte[] croppedRgba = ComposeFrame(
                    imagePtr,
                    videoWidth,
                    videoHeight,
                    out int cropX,
                    out int cropY,
                    out int cropW,
                    out int cropH);

                if (cropW <= 0 || cropH <= 0)
                {
                    // Empty frame after cropping
                    if (wasVisible)
                    {
                        long pts90 = timeMs * 90;
                        PgsEncoder.WriteClearSet(fileStream, pts90, videoWidth, videoHeight, compositionNumber);
                        compositionNumber++;
                        wasVisible = false;
                    }

                    continue;
                }

                PgsEncoder.QuantizeToPalette(croppedRgba, cropW, cropH, out byte[] indexedBitmap, out uint[] palette);

                // Write display set
                long displayPts90 = timeMs * 90;
                PgsEncoder.WriteDisplaySet(
                    fileStream,
                    displayPts90,
                    videoWidth,
                    videoHeight,
                    indexedBitmap,
                    palette,
                    cropX,
                    cropY,
                    cropW,
                    cropH,
                    compositionNumber);

                compositionNumber++;
                wasVisible = true;
            }

            // Final clear if still visible
            if (wasVisible && events.Count > 0)
            {
                long endMs = events.Max(e => e.EndMs);
                long endPts90 = endMs * 90;
                PgsEncoder.WriteClearSet(fileStream, endPts90, videoWidth, videoHeight, compositionNumber);
            }

            return true;
        }
        finally
        {
            // Cleanup native resources
            if (track != IntPtr.Zero)
            {
                LibassNative.AssFreeTrack(track);
            }

            if (renderer != IntPtr.Zero)
            {
                LibassNative.AssRendererDone(renderer);
            }

            if (library != IntPtr.Zero)
            {
                LibassNative.AssLibraryDone(library);
            }
        }
    }

    /// <summary>
    ///     Composes all ASS_Image layers into an RGBA bitmap covering only the clipped subtitle region.
    /// </summary>
    private static byte[] ComposeFrame(
        IntPtr firstImage,
        int width,
        int height,
        out int cropX,
        out int cropY,
        out int cropW,
        out int cropH)
    {
        int minX = width, minY = height, maxX = 0, maxY = 0;
        IntPtr current = firstImage;
        while (current != IntPtr.Zero)
        {
            var img = Marshal.PtrToStructure<LibassNative.AssImage>(current);

            if (img._w > 0 && img._h > 0 && img._bitmap != IntPtr.Zero)
            {
                minX = Math.Min(minX, Math.Max(img._dstX, 0));
                minY = Math.Min(minY, Math.Max(img._dstY, 0));
                maxX = Math.Max(maxX, Math.Min(img._dstX + img._w, width));
                maxY = Math.Max(maxY, Math.Min(img._dstY + img._h, height));
            }

            current = img._next;
        }

        if (maxX <= minX || maxY <= minY)
        {
            cropX = cropY = cropW = cropH = 0;
            return [];
        }

        cropX = minX;
        cropY = minY;
        cropW = maxX - minX;
        cropH = maxY - minY;
        byte[] rgba = new byte[cropW * cropH * 4];

        // Translate each layer into crop coordinates while retaining libass's blend order and source stride.
        current = firstImage;
        while (current != IntPtr.Zero)
        {
            var img = Marshal.PtrToStructure<LibassNative.AssImage>(current);
            if (img._w > 0 && img._h > 0 && img._bitmap != IntPtr.Zero)
            {
                byte r = (byte)((img._color >> 24) & 0xFF);
                byte g = (byte)((img._color >> 16) & 0xFF);
                byte b = (byte)((img._color >> 8) & 0xFF);
                byte a = (byte)(255 - (img._color & 0xFF)); // ASS alpha is inverted
                img._dstX -= cropX;
                img._dstY -= cropY;
                BlendImage(rgba, cropW, cropH, img, r, g, b, a);
            }

            current = img._next;
        }

        return rgba;
    }

    /// <summary>
    ///     Alpha-blends a single ASS_Image layer onto the RGBA canvas.
    /// </summary>
    private static void BlendImage(
        byte[] canvas,
        int canvasWidth,
        int canvasHeight,
        LibassNative.AssImage img,
        byte r,
        byte g,
        byte b,
        byte baseAlpha)
    {
        int srcStride = img._stride;

        for (int y = 0; y < img._h; y++)
        {
            int dstY = img._dstY + y;
            if (dstY < 0 || dstY >= canvasHeight)
            {
                continue;
            }

            for (int x = 0; x < img._w; x++)
            {
                int dstX = img._dstX + x;
                if (dstX < 0 || dstX >= canvasWidth)
                {
                    continue;
                }

                // Read alpha coverage from bitmap
                byte coverage = Marshal.ReadByte(img._bitmap, (y * srcStride) + x);
                if (coverage == 0)
                {
                    continue;
                }

                // Calculate effective alpha
                int alpha = ((coverage * baseAlpha) + 127) / 255;
                if (alpha == 0)
                {
                    continue;
                }

                int pixelOffset = ((dstY * canvasWidth) + dstX) * 4;

                // Read existing pixel
                byte dstR = canvas[pixelOffset];
                byte dstG = canvas[pixelOffset + 1];
                byte dstB = canvas[pixelOffset + 2];
                byte dstA = canvas[pixelOffset + 3];

                // Alpha compositing (Porter-Duff "over" operation)
                int outA = alpha + (((dstA * (255 - alpha)) + 127) / 255);
                if (outA > 0)
                {
                    canvas[pixelOffset] = (byte)(((r * alpha) + (dstR * dstA * (255 - alpha) / 255) + (outA / 2)) / outA);
                    canvas[pixelOffset + 1] = (byte)(((g * alpha) + (dstG * dstA * (255 - alpha) / 255) + (outA / 2)) / outA);
                    canvas[pixelOffset + 2] = (byte)(((b * alpha) + (dstB * dstA * (255 - alpha) / 255) + (outA / 2)) / outA);
                    canvas[pixelOffset + 3] = (byte)Math.Min(outA, 255);
                }
            }
        }
    }

    /// <summary>
    ///     Builds a sorted list of all time points that need to be rendered.
    ///     Combines event start/end times with animation frame sampling.
    /// </summary>
    private static List<long> BuildTimePoints(List<AssEvent> events, int frameIntervalMs)
    {
        var timeSet = new SortedSet<long>();

        foreach (var evt in events)
        {
            // Add event boundaries
            timeSet.Add(evt.StartMs);
            timeSet.Add(evt.EndMs);

            // Add 1ms before end to ensure we capture the last frame
            if (evt.EndMs > evt.StartMs)
            {
                timeSet.Add(evt.EndMs - 1);
            }

            // Sample intermediate frames for animation support
            bool hasAnimation = evt.HasAnimation;
            if (hasAnimation)
            {
                for (long t = evt.StartMs + frameIntervalMs; t < evt.EndMs; t += frameIntervalMs)
                {
                    timeSet.Add(t);
                }
            }
            else
            {
                // For static subtitles, only sample start + a bit after
                timeSet.Add(evt.StartMs + 1);
            }
        }

        return [.. timeSet];
    }

    /// <summary>
    ///     Parses ASS dialogue events to extract timing information.
    /// </summary>
    private static List<AssEvent> ParseAssEvents(string assFilePath)
        => ParseArtifactEvents(File.ReadAllText(assFilePath));

    private static List<AssEvent> ParseArtifactEvents(string assContent)
    {
        var events = new List<AssEvent>();
        bool inEvents = false;
        int startIdx = -1;
        int endIdx = -1;
        int textIdx = -1;

        foreach (string rawLine in assContent.Split('\n'))
        {
            string line = rawLine.Trim();

            if (line.StartsWith('['))
            {
                inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEvents)
            {
                continue;
            }

            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var columns = line[7..].Split(',').Select(s => s.Trim().ToLowerInvariant()).ToList();
                startIdx = columns.IndexOf("start");
                endIdx = columns.IndexOf("end");
                textIdx = columns.IndexOf("text");
                continue;
            }

            if (startIdx < 0 || endIdx < 0)
            {
                continue;
            }

            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string content = line[9..];
            // Split by comma, but only up to the text field (text can contain commas)
            int maxSplits = Math.Max(startIdx, Math.Max(endIdx, textIdx >= 0 ? textIdx : 0));
            string[] parts = content.Split(',', maxSplits + 1);

            if (parts.Length <= Math.Max(startIdx, endIdx))
            {
                continue;
            }

            long start = ParseAssTimestamp(parts[startIdx].Trim());
            long end = ParseAssTimestamp(parts[endIdx].Trim());

            if (start < 0 || end <= start)
            {
                continue;
            }

            bool hasAnimation = false;
            if (textIdx >= 0 && parts.Length > textIdx)
            {
                string text = parts[textIdx];
                // Check for animation tags
                hasAnimation = text.Contains("\\t(", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("\\move(", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("\\fad(", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("\\fade(", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("\\org(", StringComparison.OrdinalIgnoreCase);
            }

            events.Add(new AssEvent(start, end, hasAnimation));
        }

        return events;
    }

    /// <summary>
    ///     Parses an ASS timestamp string (H:MM:SS.CC) to milliseconds.
    /// </summary>
    private static long ParseAssTimestamp(string timestamp)
    {
        var match = AssTimestampRegex().Match(timestamp);
        if (!match.Success)
        {
            return -1;
        }

        int hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        int seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        int centiseconds = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);

        return (((hours * 3600L) + (minutes * 60L) + seconds) * 1000L) + (centiseconds * 10L);
    }

    [GeneratedRegex(@"(\d+):(\d{2}):(\d{2})\.(\d{2})")]
    private static partial Regex AssTimestampRegex();

    /// <summary>
    ///     Releases resources.
    /// </summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }

    /// <summary>
    ///     Represents a parsed ASS dialogue event with timing information.
    /// </summary>
    private sealed record AssEvent(long StartMs, long EndMs, bool HasAnimation);
}
