using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using Jellyfin.Plugin.AssSubsetter.Native;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Core;

/// <summary>
///     Service that converts ASS subtitle files to SUP (PGS) format
///     using libass for rendering and a pure C# PGS encoder for output.
/// </summary>
public class AssToSupConverter : IDisposable
{
    private readonly Func<PluginConfiguration> _configFactory;
    private readonly ILogger<AssToSupConverter> _logger;
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AssToSupConverter" /> class.
    /// </summary>
    /// <param name="configFactory">The plugin configuration factory.</param>
    /// <param name="logger">The logger instance.</param>
    public AssToSupConverter(Func<PluginConfiguration> configFactory, ILogger<AssToSupConverter> logger)
    {
        _configFactory = configFactory;
        _logger = logger;
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
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths are sanitized and guided by server side configurations.")]
    public async Task<bool> ConvertAsync(
        string inputAssPath,
        string outputSupPath,
        int videoWidth,
        int videoHeight,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("[AssSubsetter] Starting ASS to SUP conversion for {File} ({Width}x{Height})...", inputAssPath, videoWidth, videoHeight);

            // Parse ASS events to get time ranges
            var events = ParseAssEvents(inputAssPath);
            if (events.Count == 0)
            {
                _logger.LogInformation("[AssSubsetter] No dialogue events found in {File}. Skipping conversion.", inputAssPath);
                return false;
            }

            bool result = await AtomicCacheFile.WriteAsync(
                outputSupPath,
                async (partialPath, token) =>
                {
                    // Run the CPU-intensive rendering on a thread pool thread
                    bool rendered = await Task.Run(
                        () => RenderAndEncode(inputAssPath, partialPath, videoWidth, videoHeight, events, token),
                        token).ConfigureAwait(false);

                    return rendered &&
                           File.Exists(partialPath) &&
                           new FileInfo(partialPath).Length > 0;
                },
                _logger,
                cancellationToken).ConfigureAwait(false);

            if (result)
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

    private bool RenderAndEncode(
        string inputAssPath,
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

            // Set custom fonts directories
            string fontsDir = Config.CustomFontDirectories;
            if (!string.IsNullOrEmpty(fontsDir))
            {
                // ass_set_fonts_dir only takes a single directory, so we set the first one
                // and rely on fontconfig for the rest
                string[] dirs = fontsDir.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (string dir in dirs)
                {
                    if (Directory.Exists(dir))
                    {
                        LibassNative.AssSetFontsDir(library, dir);
                        _logger.LogDebug("[AssSubsetter] Set libass fonts dir: {Dir}", dir);
                        break; // libass only supports one fonts_dir
                    }
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

            // Set fonts with autodetect provider
            LibassNative.AssSetFonts(renderer, null, "sans-serif", 1, null, 1);

            // Load ASS file
            track = LibassNative.AssReadFile(library, inputAssPath, null);
            if (track == IntPtr.Zero)
            {
                _logger.LogError("[AssSubsetter] Failed to load ASS file: {File}", inputAssPath);
                return false;
            }

            // Render and encode
            int frameIntervalMs = 1000 / Math.Clamp(Config.AssToSupFrameRate, 10, 60);

            using var fileStream = new FileStream(outputSupPath, FileMode.Create, FileAccess.Write, FileShare.None);

            ushort compositionNumber = 0;
            bool wasVisible = false;
            byte[]? lastRgba = null;

            // Build sorted list of all interesting time points
            var timePoints = BuildTimePoints(events, frameIntervalMs);

            _logger.LogDebug("[AssSubsetter] Processing {Count} time points for rendering...", timePoints.Count);

            foreach (long timeMs in timePoints)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Render frame at this timestamp
                IntPtr imagePtr = LibassNative.AssRenderFrame(renderer, track, timeMs, out int changed);

                if (changed == 0 && wasVisible && lastRgba != null)
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
                        lastRgba = null;
                    }

                    continue;
                }

                // Compose ASS_Image linked list into RGBA bitmap
                byte[] rgbaFrame = ComposeFrame(
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
                        lastRgba = null;
                    }

                    continue;
                }

                // Extract cropped region
                byte[] croppedRgba = CropRgba(rgbaFrame, videoWidth, cropX, cropY, cropW, cropH);

                // Quantize to palette
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
                lastRgba = croppedRgba;
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
    ///     Composes all ASS_Image layers into a single RGBA bitmap and calculates the tight crop rectangle.
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
        byte[] rgba = new byte[width * height * 4]; // Initialized to all zeros (transparent)

        int minX = width, minY = height, maxX = 0, maxY = 0;

        IntPtr current = firstImage;
        while (current != IntPtr.Zero)
        {
            var img = Marshal.PtrToStructure<LibassNative.AssImage>(current);

            if (img.W > 0 && img.H > 0 && img.Bitmap != IntPtr.Zero)
            {
                // Extract color components from RGBA format
                byte r = (byte)((img.Color >> 24) & 0xFF);
                byte g = (byte)((img.Color >> 16) & 0xFF);
                byte b = (byte)((img.Color >> 8) & 0xFF);
                byte a = (byte)(255 - (img.Color & 0xFF)); // ASS alpha is inverted

                BlendImage(rgba, width, height, img, r, g, b, a);

                // Update bounding box
                int imgMaxX = Math.Min(img.DstX + img.W, width);
                int imgMaxY = Math.Min(img.DstY + img.H, height);
                int imgMinX = Math.Max(img.DstX, 0);
                int imgMinY = Math.Max(img.DstY, 0);

                if (imgMinX < minX)
                {
                    minX = imgMinX;
                }

                if (imgMinY < minY)
                {
                    minY = imgMinY;
                }

                if (imgMaxX > maxX)
                {
                    maxX = imgMaxX;
                }

                if (imgMaxY > maxY)
                {
                    maxY = imgMaxY;
                }
            }

            current = img.Next;
        }

        if (maxX <= minX || maxY <= minY)
        {
            cropX = cropY = cropW = cropH = 0;
        }
        else
        {
            cropX = minX;
            cropY = minY;
            cropW = maxX - minX;
            cropH = maxY - minY;
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
        int srcStride = img.Stride;

        for (int y = 0; y < img.H; y++)
        {
            int dstY = img.DstY + y;
            if (dstY < 0 || dstY >= canvasHeight)
            {
                continue;
            }

            for (int x = 0; x < img.W; x++)
            {
                int dstX = img.DstX + x;
                if (dstX < 0 || dstX >= canvasWidth)
                {
                    continue;
                }

                // Read alpha coverage from bitmap
                byte coverage = Marshal.ReadByte(img.Bitmap, (y * srcStride) + x);
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
    ///     Extracts a cropped rectangular region from the full RGBA canvas.
    /// </summary>
    private static byte[] CropRgba(byte[] rgba, int canvasWidth, int cropX, int cropY, int cropW, int cropH)
    {
        byte[] cropped = new byte[cropW * cropH * 4];

        for (int y = 0; y < cropH; y++)
        {
            int srcOffset = (((cropY + y) * canvasWidth) + cropX) * 4;
            int dstOffset = y * cropW * 4;
            Buffer.BlockCopy(rgba, srcOffset, cropped, dstOffset, cropW * 4);
        }

        return cropped;
    }

    /// <summary>
    ///     Builds a sorted list of all time points that need to be rendered.
    ///     Combines event start/end times with animation frame sampling.
    /// </summary>
    private List<long> BuildTimePoints(List<AssEvent> events, int frameIntervalMs)
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

        return timeSet.ToList();
    }

    /// <summary>
    ///     Parses ASS dialogue events to extract timing information.
    /// </summary>
    private static List<AssEvent> ParseAssEvents(string assFilePath)
    {
        var events = new List<AssEvent>();
        bool inEvents = false;
        int startIdx = -1;
        int endIdx = -1;
        int textIdx = -1;

        foreach (string rawLine in File.ReadLines(assFilePath))
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
                var columns = line.Substring(7).Split(',').Select(s => s.Trim().ToLowerInvariant()).ToList();
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

            string content = line.Substring(9);
            // Split by comma, but only up to the text field (text can contain commas)
            int maxSplits = Math.Max(startIdx, Math.Max(endIdx, textIdx >= 0 ? textIdx : 0));
            string[] parts = content.Split(',', maxSplits + 2);

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
        // Format: H:MM:SS.CC (centiseconds)
        var match = Regex.Match(timestamp, @"(\d+):(\d{2}):(\d{2})\.(\d{2})");
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
