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
    public Dictionary<FontDescriptor, HashSet<uint>> ExtractUsedCharacters(string filePath)
    {
        var usedChars = new Dictionary<FontDescriptor, HashSet<uint>>();
        var styles = new Dictionary<string, FontDescriptor>(StringComparer.OrdinalIgnoreCase);

        bool inStyles = false;
        bool inEvents = false;

        int styleNameIndex = -1;
        int styleFontIndex = -1;
        int styleBoldIndex = -1;
        int styleItalicIndex = -1;

        int eventStyleIndex = 3;
        int eventTextIndex = 9;

        foreach (var line in File.ReadLines(filePath))
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine))
            {
                continue;
            }

            if (trimmedLine.StartsWith('['))
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
                    styleBoldIndex = columns.IndexOf("bold");
                    styleItalicIndex = columns.IndexOf("italic");
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
                        if (fontName.StartsWith('@'))
                        {
                            fontName = fontName.Substring(1); // Ignore @ vertical font prefix
                        }

                        int? weight = null;
                        bool isBoldReq = false;
                        bool isItalic = false;

                        if (styleBoldIndex >= 0 && styleBoldIndex < parts.Length)
                        {
                            string bStr = parts[styleBoldIndex].Trim();
                            if (bStr == "-1")
                            {
                                isBoldReq = true;
                            }
                            else if (int.TryParse(bStr, out int w))
                            {
                                if (w == 1)
                                {
                                    isBoldReq = true;
                                }
                                else if (w > 1)
                                {
                                    weight = w;
                                }
                            }
                        }

                        if (styleItalicIndex >= 0 && styleItalicIndex < parts.Length)
                        {
                            string iStr = parts[styleItalicIndex].Trim();
                            isItalic = iStr == "-1" || iStr == "1";
                        }

                        var desc = new FontDescriptor(fontName, weight, isBoldReq, isItalic);
                        styles[name] = desc;
                        if (!usedChars.ContainsKey(desc))
                        {
                            usedChars[desc] = new HashSet<uint>();
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
                    var parts = dialogueStr.Split(',', eventTextIndex + 1);

                    if (eventStyleIndex >= 0 && eventStyleIndex < parts.Length &&
                        eventTextIndex >= 0 && eventTextIndex < parts.Length)
                    {
                        string styleName = parts[eventStyleIndex].Trim();
                        string text = parts[eventTextIndex];

                        FontDescriptor currentFont = styles.TryGetValue(styleName, out var f) ? f : new FontDescriptor("Arial", null, false, false);
                        FontDescriptor defaultFont = currentFont;

                        if (!usedChars.ContainsKey(currentFont))
                        {
                            usedChars[currentFont] = new HashSet<uint>();
                        }

                        ParseTextLine(text, currentFont, defaultFont, styles, usedChars);
                    }
                }
            }
        }

        var result = new Dictionary<FontDescriptor, HashSet<uint>>();
        foreach (var kvp in usedChars)
        {
            if (kvp.Value.Count > 0)
            {
                result[kvp.Key] = kvp.Value;
            }
        }

        return result;
    }

    private void ParseTextLine(string text, FontDescriptor currentFont, FontDescriptor defaultFont, Dictionary<string, FontDescriptor> styles, Dictionary<FontDescriptor, HashSet<uint>> usedChars)
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
                    if (newFont.StartsWith('@'))
                    {
                        newFont = newFont.Substring(1);
                    }

                    if (!string.IsNullOrEmpty(newFont))
                    {
                        currentFont = currentFont with { FontName = newFont };
                        if (!usedChars.ContainsKey(currentFont))
                        {
                            usedChars[currentFont] = new HashSet<uint>();
                        }
                    }
                }

                int bIndex = tagContent.IndexOf("\\b", StringComparison.OrdinalIgnoreCase);
                if (bIndex != -1)
                {
                    int start = bIndex + 2;
                    int end = start;
                    while (end < tagContent.Length && tagContent[end] != '\\')
                    {
                        end++;
                    }

                    string bVal = tagContent.Substring(start, end - start).Trim();
                    if (bVal == "1")
                    {
                        currentFont = currentFont with { IsBoldRequest = true, RequestedWeight = null };
                    }
                    else if (bVal == "0")
                    {
                        currentFont = currentFont with { IsBoldRequest = false, RequestedWeight = null };
                    }
                    else if (int.TryParse(bVal, out int w))
                    {
                        currentFont = currentFont with { RequestedWeight = w, IsBoldRequest = false };
                    }

                    if (!usedChars.ContainsKey(currentFont))
                    {
                        usedChars[currentFont] = new HashSet<uint>();
                    }
                }

                int iIndex = tagContent.IndexOf("\\i", StringComparison.OrdinalIgnoreCase);
                if (iIndex != -1)
                {
                    int start = iIndex + 2;
                    int end = start;
                    while (end < tagContent.Length && tagContent[end] != '\\')
                    {
                        end++;
                    }

                    string iVal = tagContent.Substring(start, end - start).Trim();
                    if (iVal == "1")
                    {
                        currentFont = currentFont with { IsItalic = true };
                    }
                    else if (iVal == "0")
                    {
                        currentFont = currentFont with { IsItalic = false };
                    }

                    if (!usedChars.ContainsKey(currentFont))
                    {
                        usedChars[currentFont] = new HashSet<uint>();
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
                        inDrawingMode = tagContent[pSearchIdx + 2] != '0';
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
