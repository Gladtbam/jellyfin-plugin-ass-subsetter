#pragma warning disable SA1513
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
/// Processor for handling ASS subtitle subsetting via local HarfBuzzSharp.
/// </summary>
public class AssProcessor
{
    private readonly ILogger<AssProcessor> _logger;
    private readonly PluginConfiguration _config;
    private readonly FontCacheManager _fontCacheManager;
    private readonly AssDocumentParser _assParser;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssProcessor"/> class.
    /// </summary>
    /// <param name="config">The plugin configuration instance.</param>
    /// <param name="fontCacheManager">The font cache manager.</param>
    /// <param name="assParser">The ASS document parser.</param>
    /// <param name="logger">The logger instance.</param>
    public AssProcessor(PluginConfiguration config, FontCacheManager fontCacheManager, AssDocumentParser assParser, ILogger<AssProcessor> logger)
    {
        _logger = logger;
        _config = config;
        _fontCacheManager = fontCacheManager;
        _assParser = assParser;
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
        try
        {
            _logger.LogInformation("[AssSubsetter] Starting font subsetting for {File}...", inputAssPath);

            // 1. Parse ASS to get required characters per font
            var usedChars = _assParser.ExtractUsedCharacters(inputAssPath);
            if (usedChars.Count == 0)
            {
                _logger.LogInformation("[AssSubsetter] No font usage found in {File}. Copying as is.", inputAssPath);
                File.Copy(inputAssPath, outputCachePath, true);
                return true;
            }

            // 2. Ensure font cache is loaded
            await _fontCacheManager.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

            // 3. Prepare the output file by copying the original
            string? outDir = Path.GetDirectoryName(outputCachePath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }
            File.Copy(inputAssPath, outputCachePath, true);

            // 4. Subset fonts and append to ASS
            using var writer = new StreamWriter(outputCachePath, append: true);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync("[Fonts]").ConfigureAwait(false);

            int subsetCount = 0;

            foreach (var kvp in usedChars)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string fontName = kvp.Key;
                var codepoints = kvp.Value;

                var fontInfo = _fontCacheManager.FindFontFilePath(fontName);
                if (fontInfo == null || string.IsNullOrEmpty(fontInfo.Value.Path))
                {
                    _logger.LogWarning("[AssSubsetter] Could not find physical font file for '{FontName}'. Skipping.", fontName);
                    continue;
                }

                string physicalPath = fontInfo.Value.Path;
                int faceIndex = fontInfo.Value.FaceIndex;

                _logger.LogInformation("[AssSubsetter] Subsetting font '{FontName}' from {Path} (Face {FaceIndex}) ({Count} characters)...", fontName, physicalPath, faceIndex, codepoints.Count);

                byte[] fontData = await File.ReadAllBytesAsync(physicalPath, cancellationToken).ConfigureAwait(false);
                byte[]? subsetData = HarfBuzzSubsetNative.SubsetFont(fontData, (uint)faceIndex, codepoints, _logger);

                if (subsetData != null && subsetData.Length > 0)
                {
                    // Use clean standard filename for the embedded font (remove invalid OS characters just in case)
                    string safeFontName = string.Join("_", fontName.Split(Path.GetInvalidFileNameChars()));
                    string embeddedName = $"{safeFontName}.ttf";
                    await writer.WriteLineAsync($"fontname: {embeddedName}").ConfigureAwait(false);

                    var encodedLines = EncodeFontToAssLines(subsetData);
                    foreach (var line in encodedLines)
                    {
                        await writer.WriteLineAsync(line).ConfigureAwait(false);
                    }
                    await writer.WriteLineAsync().ConfigureAwait(false);
                    subsetCount++;
                }
            }

            _logger.LogInformation("[AssSubsetter] Completed subsetting. Successfully embedded {Count} fonts.", subsetCount);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AssSubsetter] Exception occurred while generating subset font.");
            return false;
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
