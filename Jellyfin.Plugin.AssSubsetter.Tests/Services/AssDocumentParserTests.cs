using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Services;

public class AssDocumentParserTests : IDisposable
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

        Assert.Contains("Arial", (IDictionary<string, HashSet<uint>>)result);
        var codepoints = result["Arial"];
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

        Assert.Contains("Arial", (IDictionary<string, HashSet<uint>>)result);
        Assert.Contains("Comic Sans MS", (IDictionary<string, HashSet<uint>>)result);

        var arialChars = result["Arial"];
        var comicChars = result["Comic Sans MS"];

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

        Assert.Contains("Arial", (IDictionary<string, HashSet<uint>>)result);
        Assert.Contains("Times New Roman", (IDictionary<string, HashSet<uint>>)result);

        var arialChars = result["Arial"];
        var timesChars = result["Times New Roman"];

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

        Assert.Contains("Arial", (IDictionary<string, HashSet<uint>>)result);
        var codepoints = result["Arial"];

        // "m 0 0 l 100 100" should be ignored because it's inside \p1 ... \p0
        Assert.DoesNotContain((uint)'m', codepoints);
        Assert.DoesNotContain((uint)'0', codepoints);
        Assert.DoesNotContain((uint)'1', codepoints);

        Assert.Contains((uint)'H', codepoints);
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

        Assert.Contains("Arial", (IDictionary<string, HashSet<uint>>)result);
        var codepoints = result["Arial"];

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
