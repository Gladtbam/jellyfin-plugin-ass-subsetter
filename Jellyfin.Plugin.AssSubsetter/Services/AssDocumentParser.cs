#pragma warning disable SA1300, SA1600, SA1611, SA1615, SA1503, CA1854, CA1861, CA1865, CA1869, SA1516, SA1028, CA5392, SA1513, SA1649, SA1402, CS1591, SA1119
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
/// A lightweight parser for Advanced SubStation Alpha (ASS) files, extracting required font characters.
/// </summary>
public class AssDocumentParser
{
    /// <summary>
    /// Parses an ASS file and extracts the unique Unicode codepoints used per font.
    /// </summary>
    /// <param name="filePath">The ASS file path.</param>
    /// <returns>A dictionary mapping FontName to a HashSet of used Unicode codepoints.</returns>
    public Dictionary<string, HashSet<uint>> ExtractUsedCharacters(string filePath)
    {
        var usedChars = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        var styles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        bool inStyles = false;
        bool inEvents = false;

        int styleNameIndex = 0;
        int styleFontIndex = 1;

        int eventStyleIndex = 3;
        int eventTextIndex = 9;

        foreach (var line in File.ReadLines(filePath))
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine))
            {
                continue;
            }

            if (trimmedLine.StartsWith("[", StringComparison.Ordinal))
            {
                inStyles = trimmedLine.Equals("[V4+ Styles]", StringComparison.OrdinalIgnoreCase) || 
                           trimmedLine.Equals("[V4 Styles]", StringComparison.OrdinalIgnoreCase);
                inEvents = trimmedLine.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inStyles)
            {
                if (trimmedLine.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    var formatStr = trimmedLine.Substring(7).Trim();
                    var columns = formatStr.Split(',').Select(s => s.Trim().ToLowerInvariant()).ToList();
                    styleNameIndex = columns.IndexOf("name");
                    styleFontIndex = columns.IndexOf("fontname");
                }
                else if (trimmedLine.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
                {
                    var styleStr = trimmedLine.Substring(6).Trim();
                    var parts = styleStr.Split(',');
                    if (styleNameIndex >= 0 && styleNameIndex < parts.Length &&
                        styleFontIndex >= 0 && styleFontIndex < parts.Length)
                    {
                        string name = parts[styleNameIndex].Trim();
                        string fontName = parts[styleFontIndex].Trim();
                        if (fontName.StartsWith("@", StringComparison.Ordinal))
                        {
                            fontName = fontName.Substring(1); // Ignore @ vertical font prefix
                        }

                        styles[name] = fontName;
                        if (!usedChars.ContainsKey(fontName))
                        {
                            usedChars[fontName] = new HashSet<uint>();
                        }
                    }
                }
            }
            else if (inEvents)
            {
                if (trimmedLine.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    var formatStr = trimmedLine.Substring(7).Trim();
                    var columns = formatStr.Split(',').Select(s => s.Trim().ToLowerInvariant()).ToList();
                    eventStyleIndex = columns.IndexOf("style");
                    eventTextIndex = columns.IndexOf("text");
                }
                else if (trimmedLine.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                {
                    var dialogueStr = trimmedLine.Substring(9).Trim();
                    var parts = dialogueStr.Split(new[] { ',' }, eventTextIndex + 1); 
                    
                    if (eventStyleIndex >= 0 && eventStyleIndex < parts.Length &&
                        eventTextIndex >= 0 && eventTextIndex < parts.Length)
                    {
                        string styleName = parts[eventStyleIndex].Trim();
                        string text = parts[eventTextIndex];

                        string currentFont = styles.TryGetValue(styleName, out var f) ? f : "Arial";
                        string defaultFont = currentFont;

                        if (!usedChars.ContainsKey(currentFont))
                        {
                            usedChars[currentFont] = new HashSet<uint>();
                        }

                        ParseTextLine(text, currentFont, defaultFont, styles, usedChars);
                    }
                }
            }
        }

        var result = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in usedChars)
        {
            if (kvp.Value.Count > 0)
            {
                result[kvp.Key] = kvp.Value;
            }
        }

        return result;
    }

    private void ParseTextLine(string text, string currentFont, string defaultFont, Dictionary<string, string> styles, Dictionary<string, HashSet<uint>> usedChars)
    {
        bool inDrawingMode = false;
        
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{')
            {
                int endTag = text.IndexOf('}', i);
                if (endTag == -1)
                {
                    endTag = text.Length;
                }
                
                string tagContent = text.Substring(i + 1, endTag - i - 1);
                
                int fnIndex = tagContent.IndexOf("\\fn", StringComparison.OrdinalIgnoreCase);
                if (fnIndex != -1)
                {
                    int start = fnIndex + 3;
                    int end = start;
                    while (end < tagContent.Length && tagContent[end] != '\\')
                    {
                        end++;
                    }
                    
                    string newFont = tagContent.Substring(start, end - start).Trim();
                    if (newFont.StartsWith("@", StringComparison.Ordinal))
                    {
                        newFont = newFont.Substring(1);
                    }

                    if (!string.IsNullOrEmpty(newFont))
                    {
                        currentFont = newFont;
                        if (!usedChars.ContainsKey(currentFont))
                        {
                            usedChars[currentFont] = new HashSet<uint>();
                        }
                    }
                }

                int rIndex = tagContent.IndexOf("\\r", StringComparison.OrdinalIgnoreCase);
                if (rIndex != -1)
                {
                    int start = rIndex + 2;
                    int end = start;
                    while (end < tagContent.Length && tagContent[end] != '\\')
                    {
                        end++;
                    }
                    
                    string overrideStyle = tagContent.Substring(start, end - start).Trim();
                    if (string.IsNullOrEmpty(overrideStyle))
                    {
                        currentFont = defaultFont;
                    }
                    else if (styles.TryGetValue(overrideStyle, out var of))
                    {
                        currentFont = of;
                    }
                    
                    if (!usedChars.ContainsKey(currentFont))
                    {
                        usedChars[currentFont] = new HashSet<uint>();
                    }
                }

                int pSearchIdx = 0;
                while ((pSearchIdx = tagContent.IndexOf("\\p", pSearchIdx, StringComparison.OrdinalIgnoreCase)) != -1)
                {
                    if (pSearchIdx + 2 < tagContent.Length && char.IsDigit(tagContent[pSearchIdx + 2]))
                    {
                        inDrawingMode = (tagContent[pSearchIdx + 2] != '0');
                        break;
                    }
                    pSearchIdx += 2;
                }

                i = endTag;
            }
            else
            {
                if (inDrawingMode)
                {
                    continue;
                }

                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    uint codepoint = (uint)char.ConvertToUtf32(c, text[i + 1]);
                    usedChars[currentFont].Add(codepoint);
                    i++;
                }
                else
                {
                    if (c == '\\' && i + 1 < text.Length)
                    {
                        char next = text[i + 1];
                        if (next == 'N' || next == 'n' || next == 'h')
                        {
                            i++;
                            continue;
                        }
                    }
                    usedChars[currentFont].Add(c);
                }
            }
        }
    }
}
