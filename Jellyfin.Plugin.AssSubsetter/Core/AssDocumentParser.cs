using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.AssSubsetter.Models;

namespace Jellyfin.Plugin.AssSubsetter.Core;

/// <summary>
///     A lightweight parser for Advanced SubStation Alpha (ASS) files, extracting required font characters.
/// </summary>
public class AssDocumentParser
{
    /// <summary>
    ///     Parses an ASS file and extracts the unique Unicode codepoints used per font.
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

        // codeql[cs/linq/missed-select] Justification: Traditional foreach avoids LINQ delegate allocation overhead in hot paths.
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
                        string name = NormalizeStyleName(parts[styleNameIndex].Trim());
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
                        string styleName = NormalizeStyleName(parts[eventStyleIndex].Trim());

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
        // codeql[cs/linq/missed-where] Justification: Traditional foreach avoids LINQ delegate allocation overhead in hot paths.
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

                ApplyOverrideTags(tagContent, ref currentFont, defaultFont, styles, ref inDrawingMode);
                if (!usedChars.ContainsKey(currentFont))
                {
                    usedChars[currentFont] = new HashSet<uint>();
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

    private static void ApplyOverrideTags(
        string content,
        ref FontDescriptor currentFont,
        FontDescriptor defaultFont,
        Dictionary<string, FontDescriptor> styles,
        ref bool inDrawingMode)
    {
        int position = 0;
        while (position < content.Length)
        {
            int slash = content.IndexOf('\\', position);
            if (slash < 0)
            {
                break;
            }

            int start = slash + 1;
            int nameEnd = start;
            // Font and reset names are free-form strings, including letters and parentheses.
            if (content.AsSpan(start).StartsWith("fn", StringComparison.OrdinalIgnoreCase))
            {
                nameEnd += 2;
            }
            else if (start < content.Length && (content[start] == 'r' || content[start] == 'R'))
            {
                nameEnd++;
            }
            else
            {
                while (nameEnd < content.Length && char.IsLetter(content[nameEnd]))
                {
                    nameEnd++;
                }
            }

            string name = content.Substring(start, nameEnd - start).ToLowerInvariant();
            int end = nameEnd;
            int depth = 0;
            bool stringArgument = name is "fn" or "r";
            while (end < content.Length)
            {
                char c = content[end];
                if (c == '\\' && (stringArgument || depth == 0))
                {
                    break;
                }

                if (!stringArgument)
                {
                    if (c == '(')
                    {
                        depth++;
                    }
                    else if (c == ')' && depth > 0)
                    {
                        depth--;
                    }
                }

                end++;
            }

            // Parenthesized tags (notably nested transforms) are consumed as a unit.
            // Their children must not leak into the static font/drawing state.
            string value = content.Substring(nameEnd, end - nameEnd).Trim();
            position = end;
            switch (name)
            {
                case "fn":
                    if (value.StartsWith('@'))
                    {
                        value = value.Substring(1);
                    }

                    currentFont = currentFont with { FontName = value.Length == 0 ? defaultFont.FontName : value };
                    break;
                case "b":
                    if (int.TryParse(value, out int weight))
                    {
                        currentFont = currentFont with
                        {
                            IsBoldRequest = weight == 1,
                            RequestedWeight = weight is 0 or 1 ? null : weight
                        };
                    }

                    break;
                case "i":
                    if (value is "0" or "1")
                    {
                        currentFont = currentFont with { IsItalic = value == "1" };
                    }

                    break;
                case "r":
                    currentFont = styles.TryGetValue(NormalizeStyleName(value), out var resetFont) ? resetFont : defaultFont;
                    inDrawingMode = false;
                    break;
                case "p":
                    if (int.TryParse(value, out int drawingScale))
                    {
                        inDrawingMode = drawingScale > 0;
                    }

                    break;
            }
        }
    }

    private static string NormalizeStyleName(string styleName)
    {
        if (string.IsNullOrEmpty(styleName))
        {
            return styleName;
        }

        if (styleName.StartsWith('*') || styleName.StartsWith('@'))
        {
            return styleName.Substring(1);
        }

        return styleName;
    }
}
