using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Core;

public sealed class AssDocumentParserTests : IDisposable
{
    private readonly string _tempFile;
    private readonly AssDocumentParser _parser;

    public AssDocumentParserTests()
    {
        _tempFile = Path.GetTempFileName();
        _parser = new AssDocumentParser();
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    private void WriteAss(string content)
    {
        File.WriteAllText(_tempFile, content);
    }

    [Fact]
    public void ExtractUsedCharacters_ShouldExtractBasicTextWithDefaultStyle()
    {
        var assContent = @"
[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,2,2,10,10,10,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:00:05.00,Default,,0,0,0,,Hello
";
        WriteAss(assContent);

        var result = _parser.ExtractUsedCharacters(_tempFile);

        var desc = new FontDescriptor("Arial", null, false, false);
        Assert.True(result.TryGetValue(desc, out var codepoints));
        Assert.Contains((uint)'H', codepoints);
        Assert.Contains((uint)'e', codepoints);
        Assert.Contains((uint)'l', codepoints);
        Assert.Contains((uint)'o', codepoints);
        Assert.Equal(4, codepoints.Count); // 'H', 'e', 'l', 'o'
    }

    [Fact]
    public void ExtractUsedCharacters_ShouldHandleFnOverride()
    {
        var assContent = @"
[V4+ Styles]
Format: Name, Fontname
Style: Default,Arial

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:00:05.00,Default,,0,0,0,,Hello {\fnComic Sans MS}World
";
        WriteAss(assContent);

        var result = _parser.ExtractUsedCharacters(_tempFile);

        var arialDesc = new FontDescriptor("Arial", null, false, false);
        var comicDesc = new FontDescriptor("Comic Sans MS", null, false, false);

        Assert.True(result.TryGetValue(arialDesc, out var arialChars));
        Assert.True(result.TryGetValue(comicDesc, out var comicChars));

        Assert.Contains((uint)'H', arialChars);
        Assert.Contains((uint)'e', arialChars);
        Assert.Contains((uint)'W', comicChars);
        Assert.Contains((uint)'o', comicChars);
        Assert.Contains((uint)'r', comicChars);
        Assert.Contains((uint)'l', comicChars);
        Assert.Contains((uint)'d', comicChars);
    }

    [Fact]
    public void ExtractUsedCharacters_ShouldHandleResetTag()
    {
        var assContent = @"
[V4+ Styles]
Format: Name, Fontname
Style: Default,Arial
Style: Alt,Times New Roman

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:00:05.00,Default,,0,0,0,,One {\rAlt}Two {\r}Three
";
        WriteAss(assContent);

        var result = _parser.ExtractUsedCharacters(_tempFile);

        var desc1 = new FontDescriptor("Arial", null, false, false);
        var desc2 = new FontDescriptor("Times New Roman", null, false, false);

        Assert.True(result.TryGetValue(desc1, out var arialChars));
        Assert.True(result.TryGetValue(desc2, out var timesChars));

        // "One " and "Three" belong to Arial
        Assert.Contains((uint)'O', arialChars);
        Assert.Contains((uint)'n', arialChars);
        Assert.Contains((uint)'e', arialChars);
        Assert.Contains((uint)'T', arialChars);
        Assert.Contains((uint)'h', arialChars);
        Assert.Contains((uint)'r', arialChars);

        // "Two " belongs to Times New Roman
        Assert.Contains((uint)'T', timesChars);
        Assert.Contains((uint)'w', timesChars);
        Assert.Contains((uint)'o', timesChars);
    }

    [Fact]
    public void ExtractUsedCharacters_ShouldIgnoreDrawingTags()
    {
        var assContent = @"
[V4+ Styles]
Format: Name, Fontname
Style: Default,Arial

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:00:05.00,Default,,0,0,0,,{\p1}m 0 0 l 100 100{\p0}Hello
";
        WriteAss(assContent);

        var result = _parser.ExtractUsedCharacters(_tempFile);

        var desc = new FontDescriptor("Arial", null, false, false);
        Assert.True(result.TryGetValue(desc, out var codepoints));

        // "m 0 0 l 100 100" should be ignored because it's inside \p1 ... \p0
        Assert.DoesNotContain((uint)'m', codepoints);
        Assert.DoesNotContain((uint)'0', codepoints);
        Assert.DoesNotContain((uint)'1', codepoints);

        Assert.Contains((uint)'H', codepoints);
    }

    [Theory]
    [InlineData(@"{\bord2\b1}A", "Arial", true, false, null)]
    [InlineData(@"{\r\b1}A", "Arial", true, false, null)]
    [InlineData(@"{\b1\r}A", "Arial", false, false, null)]
    [InlineData(@"{\b0\b1\b0\b1}A", "Arial", true, false, null)]
    [InlineData(@"{\b1\b600}A", "Arial", false, false, 600)]
    [InlineData(@"{\b600\b0}A", "Arial", false, false, null)]
    [InlineData(@"{\iclip(0,0,10,10)\i1}A", "Arial", false, true, null)]
    [InlineData(@"{\i1\i0\i1}A", "Arial", false, true, null)]
    [InlineData(@"{\fnFirst\fn@Comic Sans MS}A", "Comic Sans MS", false, false, null)]
    [InlineData(@"{\fnFont (Display)}A", "Font (Display)", false, false, null)]
    [InlineData(@"{\fnFirst\fn}A", "Arial", false, false, null)]
    [InlineData(@"{\rAlt\b0\i0}A", "Times New Roman", false, false, null)]
    [InlineData(@"{\rAlt\r\b1}A", "Arial", true, false, null)]
    [InlineData(@"{\fnFirst\rMissing}A", "Arial", false, false, null)]
    [InlineData(@"{\blur1\bord2\be1\ishad1\pbo3\pos(1,2)}A", "Arial", false, false, null)]
    [InlineData(@"{\b1\t(0,100,\fnOther\i1\b0)}A", "Arial", true, false, null)]
    [InlineData(@"{\t(0,100,\t(\fnOther)\i1)\b1}A", "Arial", true, false, null)]
    [InlineData(@"{\t(\clip(0,0,10,10)\fnOther)\fnActual}A", "Actual", false, false, null)]
    [InlineData(@"{\t(\fnOther\b1}A", "Arial", false, false, null)]
    public void ExtractUsedCharacters_ShouldApplyTopLevelOverridesInOrder(string text, string fontName,
        bool bold, bool italic, int? weight)
    {
        WriteOverrideAss(text);

        var result = _parser.ExtractUsedCharacters(_tempFile);

        var entry = Assert.Single(result);
        Assert.Equal(new FontDescriptor(fontName, weight, bold, italic), entry.Key);
        Assert.Equal(new uint[] { 'A' }, entry.Value);
    }

    [Theory]
    [InlineData(@"{\p1\p0}A")]
    [InlineData(@"{\p0\p1}m 0 0 l 1 1{\p0}A")]
    [InlineData(@"{\p01}m 0 0{\p0}A")]
    [InlineData(@"{\p 1}m 0 0{\p0}A")]
    [InlineData(@"{\p1}m 0 0{\r}A")]
    [InlineData(@"{\p1\r}A")]
    [InlineData(@"{\r\p1}m 0 0{\p0}A")]
    [InlineData(@"{\t(\p1)}A")]
    public void ExtractUsedCharacters_ShouldTrackDrawingTransitionsInOrder(string text)
    {
        WriteOverrideAss(text);

        var result = _parser.ExtractUsedCharacters(_tempFile);

        var entry = Assert.Single(result);
        Assert.Equal(new FontDescriptor("Arial", null, false, false), entry.Key);
        Assert.Equal(new uint[] { 'A' }, entry.Value);
    }

    [Fact]
    public void ExtractUsedCharacters_ShouldAssignCharactersAcrossOverrideBlocks()
    {
        WriteOverrideAss(@"A{\bord2\b1}B{\r\i1}C{\rAlt}D{\r}E");

        var result = _parser.ExtractUsedCharacters(_tempFile);

        Assert.Equal(4, result.Count);
        Assert.True(result[new FontDescriptor("Arial", null, false, false)].SetEquals(new uint[] { 'A', 'E' }));
        Assert.Equal(new uint[] { 'B' }, result[new FontDescriptor("Arial", null, true, false)]);
        Assert.Equal(new uint[] { 'C' }, result[new FontDescriptor("Arial", null, false, true)]);
        Assert.Equal(new uint[] { 'D' }, result[new FontDescriptor("Times New Roman", null, true, true)]);
    }

    private void WriteOverrideAss(string text)
    {
        WriteAss("[V4+ Styles]\nFormat: Name, Fontname, Bold, Italic\n" +
                 "Style: Default,Arial,0,0\nStyle: Alt,Times New Roman,-1,-1\n" +
                 "[Events]\nFormat: Style, Text\nDialogue: Default," + text);
    }

    [Fact]
    public void ExtractUsedCharacters_ShouldParseSurrogatePairsCorrectly()
    {
        var assContent = @"
[V4+ Styles]
Format: Name, Fontname
Style: Default,Arial

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:00:05.00,Default,,0,0,0,,𠮷野家
";
        WriteAss(assContent);

        var result = _parser.ExtractUsedCharacters(_tempFile);

        var desc = new FontDescriptor("Arial", null, false, false);
        Assert.True(result.TryGetValue(desc, out var codepoints));

        // '𠮷' is U+20BB7 (134071 in decimal)
        Assert.Contains((uint)0x20BB7, codepoints);
        // '野' is U+91CE
        Assert.Contains((uint)'野', codepoints);
        // '家' is U+5BB6
        Assert.Contains((uint)'家', codepoints);

        // Should not contain the high/low surrogate parts independently
        Assert.DoesNotContain((uint)0xD842, codepoints);
        Assert.DoesNotContain((uint)0xDFB7, codepoints);
    }
}
