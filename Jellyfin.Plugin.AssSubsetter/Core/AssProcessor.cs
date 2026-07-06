using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Native;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Core;

/// <summary>
/// Processor for handling ASS subtitle subsetting via local HarfBuzzSharp.
/// </summary>
public class AssProcessor
{
    private readonly ILogger<AssProcessor> _logger;
    private readonly Func<PluginConfiguration> _configFactory;
    private readonly FontCacheManager _fontCacheManager;
    private readonly AssDocumentParser _assParser;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssProcessor"/> class.
    /// </summary>
    /// <param name="configFactory">The configuration factory.</param>
    /// <param name="fontCacheManager">The font cache manager.</param>
    /// <param name="assParser">The ASS document parser.</param>
    /// <param name="logger">The logger.</param>
    public AssProcessor(Func<PluginConfiguration> configFactory, FontCacheManager fontCacheManager, AssDocumentParser assParser, ILogger<AssProcessor> logger)
    {
        _logger = logger;
        _configFactory = configFactory;
        _fontCacheManager = fontCacheManager;
        _assParser = assParser;
    }

    private PluginConfiguration Config => _configFactory();

    /// <summary>
    /// Generates a subsetted ASS subtitle file with font renaming for correct player matching.
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

            // Parse ASS to get required characters per font
            var usedChars = _assParser.ExtractUsedCharacters(inputAssPath);
            if (usedChars.Count == 0)
            {
                _logger.LogInformation("[AssSubsetter] No font usage found in {File}. Copying as is.", inputAssPath);
                File.Copy(inputAssPath, outputCachePath, true);
                return true;
            }

            // Ensure font cache is loaded
            await _fontCacheManager.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

            // Prepare output directory
            string? outDir = Path.GetDirectoryName(outputCachePath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            // Subset fonts, rename, and collect name mapping
            // Key: original font name (case-insensitive) → Value: new random prefix name
            var fontNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var embeddedFonts = new List<(string EmbeddedName, byte[] Data)>();
            int subsetCount = 0;

            foreach (var kvp in usedChars)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FontDescriptor desc = kvp.Key;
                string fontName = desc.FontName;
                var codepoints = kvp.Value;

                // Skip if we already generated a prefix for this font family name
                // (multiple variants like Bold/Italic share the same prefix)
                if (!fontNameMap.TryGetValue(fontName, out string? newPrefix))
                {
                    newPrefix = FontNameRewriter.GenerateRandomPrefix();
                    fontNameMap[fontName] = newPrefix;
                }

                var fontInfo = _fontCacheManager.FindFontFilePath(desc);
                if (fontInfo == null || string.IsNullOrEmpty(fontInfo.Value.Path))
                {
                    _logger.LogWarning("[AssSubsetter] Could not find physical font file for '{FontName}' (Variant requested: {Desc}). Skipping.", fontName, desc);
                    continue;
                }

                string physicalPath = fontInfo.Value.Path;
                int faceIndex = fontInfo.Value.FaceIndex;

                _logger.LogDebug("[AssSubsetter] Subsetting font '{FontName}' from {Path} (Face {FaceIndex}) ({Count} characters)...", fontName, physicalPath, faceIndex, codepoints.Count);

                byte[] fontData = await File.ReadAllBytesAsync(physicalPath, cancellationToken).ConfigureAwait(false);
                byte[]? subsetData = HarfBuzzSubsetNative.SubsetFont(fontData, (uint)faceIndex, codepoints, _logger);

                if (subsetData != null && subsetData.Length > 0)
                {
                    // Rename font family in the subsetted binary
                    byte[]? renamedData = FontNameRewriter.RenameFontFamily(subsetData, newPrefix);
                    if (renamedData == null)
                    {
                        _logger.LogWarning("[AssSubsetter] Failed to rename font '{FontName}' to '{Prefix}'. Using subsetted font without renaming.", fontName, newPrefix);
                        renamedData = subsetData;
                    }
                    else
                    {
                        _logger.LogDebug("[AssSubsetter] Renamed font '{FontName}' → '{Prefix}'", fontName, newPrefix);
                    }

                    string embeddedName = $"{newPrefix}.ttf";
                    embeddedFonts.Add((embeddedName, renamedData));
                    subsetCount++;
                }
            }

            // Read original ASS content and rewrite font name references
            string originalContent = await File.ReadAllTextAsync(inputAssPath, cancellationToken).ConfigureAwait(false);
            string rewrittenContent = RewriteAssFontNames(originalContent, fontNameMap);

            // Write the modified ASS content + [Fonts] section to output
            await File.WriteAllTextAsync(outputCachePath, rewrittenContent, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            using var writer = new StreamWriter(outputCachePath, append: true, new UTF8Encoding(false));
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync("[Fonts]").ConfigureAwait(false);

            foreach (var (embeddedName, data) in embeddedFonts)
            {
                await writer.WriteLineAsync($"fontname: {embeddedName}").ConfigureAwait(false);

                var encodedLines = EncodeFontToAssLines(data);
                foreach (var line in encodedLines)
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }

                await writer.WriteLineAsync().ConfigureAwait(false);
            }

            _logger.LogInformation("[AssSubsetter] Completed subsetting. Successfully embedded {Count} fonts with name rewriting.", subsetCount);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "[AssSubsetter] IO Exception occurred while generating subset font.");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "[AssSubsetter] Unauthorized access exception occurred while generating subset font.");
            return false;
        }

        // codeql[cs/catch-of-all-exceptions] Justification: Interacting with native HarfBuzz/Skia APIs can throw unpredictable managed exceptions.
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AssSubsetter] Unexpected exception occurred while generating subset font.");
            return false;
        }
    }

    /// <summary>
    /// Rewrites font name references in ASS content, replacing original font names with
    /// their mapped random prefix names in both [V4+ Styles] Fontname fields and \fn override tags.
    /// </summary>
    /// <param name="content">The original ASS file content.</param>
    /// <param name="fontNameMap">Mapping from original font names to new prefix names (case-insensitive keys).</param>
    /// <returns>The rewritten ASS content with updated font name references.</returns>
    internal static string RewriteAssFontNames(string content, Dictionary<string, string> fontNameMap)
    {
        if (fontNameMap.Count == 0)
        {
            return content;
        }

        var lines = content.Split('\n');
        var result = new StringBuilder(content.Length);

        bool inStyles = false;
        bool inEvents = false;

        int styleFontIndex = -1;

        for (int li = 0; li < lines.Length; li++)
        {
            // Preserve original line endings
            string line = lines[li].TrimEnd('\r');

            var trimmed = line.Trim();

            if (trimmed.StartsWith('['))
            {
                inStyles = trimmed.Equals("[V4+ Styles]", StringComparison.OrdinalIgnoreCase) ||
                           trimmed.Equals("[V4 Styles]", StringComparison.OrdinalIgnoreCase);
                inEvents = trimmed.Equals("[Events]", StringComparison.OrdinalIgnoreCase);

                result.Append(line);
                if (li < lines.Length - 1)
                {
                    result.Append("\r\n");
                }

                continue;
            }

            string outputLine;

            if (inStyles)
            {
                if (trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    var formatStr = trimmed.Substring(7).Trim();
                    var columns = formatStr.Split(',').Select(s => s.Trim().ToLowerInvariant()).ToList();
                    styleFontIndex = columns.IndexOf("fontname");
                    outputLine = line;
                }
                else if (trimmed.StartsWith("Style:", StringComparison.OrdinalIgnoreCase) && styleFontIndex >= 0)
                {
                    outputLine = RewriteStyleLine(line, styleFontIndex, fontNameMap);
                }
                else
                {
                    outputLine = line;
                }
            }
            else if (inEvents)
            {
                if (trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
                {
                    outputLine = RewriteFnTags(line, fontNameMap);
                }
                else
                {
                    outputLine = line;
                }
            }
            else
            {
                outputLine = line;
            }

            result.Append(outputLine);
            if (li < lines.Length - 1)
            {
                result.Append("\r\n");
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Rewrites the Fontname field in a Style: line, replacing the font name with its mapped prefix.
    /// Preserves the @ vertical font prefix if present.
    /// </summary>
    private static string RewriteStyleLine(string line, int fontIndex, Dictionary<string, string> fontNameMap)
    {
        // Style: name,fontname,size,...
        int colonPos = line.IndexOf(':', StringComparison.Ordinal);
        if (colonPos < 0)
        {
            return line;
        }

        string prefix = line.Substring(0, colonPos + 1);
        string rest = line.Substring(colonPos + 1);

        // Split carefully — Style fields are comma-separated
        var parts = rest.Split(',');
        if (fontIndex >= parts.Length)
        {
            return line;
        }

        string originalFontField = parts[fontIndex];
        string trimmedFont = originalFontField.Trim();

        bool hasVerticalPrefix = trimmedFont.StartsWith('@');
        string lookupName = hasVerticalPrefix ? trimmedFont.Substring(1) : trimmedFont;

        if (fontNameMap.TryGetValue(lookupName, out string? newName))
        {
            // Preserve leading whitespace from original field
            int firstNonSpace = 0;
            while (firstNonSpace < originalFontField.Length && originalFontField[firstNonSpace] == ' ')
            {
                firstNonSpace++;
            }

            string leadingSpace = firstNonSpace > 0
                ? originalFontField.Substring(0, firstNonSpace)
                : string.Empty;

            parts[fontIndex] = hasVerticalPrefix
                ? $"{leadingSpace}@{newName}"
                : $"{leadingSpace}{newName}";
        }

        return prefix + string.Join(",", parts);
    }

    /// <summary>
    /// Rewrites \fn override tags in a Dialogue/Comment line, replacing font names with mapped prefix names.
    /// </summary>
    private static string RewriteFnTags(string line, Dictionary<string, string> fontNameMap)
    {
        // Match \fn followed by the font name (up to the next \ or })
        // Pattern: \fn[@]FontName where FontName continues until \ or }
        return Regex.Replace(line, @"\\fn(@?)([^\\}]+)", match =>
        {
            string verticalPrefix = match.Groups[1].Value;
            string fontName = match.Groups[2].Value.Trim();

            if (fontNameMap.TryGetValue(fontName, out string? newName))
            {
                return $"\\fn{verticalPrefix}{newName}";
            }

            return match.Value;
        });
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
