using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Native;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Checks whether libass can render a mapped glyph using only the supplied font attachment.</summary>
internal static class LibassFontVerifier
{
    private static readonly Lazy<bool> Available = new(CheckAvailability);
    private static readonly LibassNative.AssLogCallback SilentLog = (_, _, _, _) => { };

    internal static bool IsAvailable => Available.Value;

    internal static unsafe FontVerificationStatus Verify(byte[] data, InspectedFontFace inspected)
    {
        if (!IsAvailable)
        {
            return FontVerificationStatus.Unknown;
        }

        if (data.Length == 0 || inspected.FamilyName.IndexOfAny([',', '\r', '\n']) >= 0)
        {
            return FontVerificationStatus.Failed;
        }

        uint scalar = inspected.UnicodeMappings.Keys
            .Where(IsPrintable)
            .DefaultIfEmpty(0U)
            .Min();
        if (scalar == 0)
        {
            return FontVerificationStatus.Unknown;
        }

        IntPtr library = IntPtr.Zero;
        IntPtr renderer = IntPtr.Zero;
        IntPtr track = IntPtr.Zero;
        try
        {
            library = LibassNative.AssLibraryInit();
            if (library == IntPtr.Zero)
            {
                return FontVerificationStatus.Failed;
            }

            LibassNative.AssSetMessageCb(library, SilentLog, IntPtr.Zero);
            fixed (byte* fontData = data)
            {
                LibassNative.AssAddFont(library, "quality-probe.ttf", fontData, data.Length);
            }

            renderer = LibassNative.AssRendererInit(library);
            if (renderer == IntPtr.Zero)
            {
                return FontVerificationStatus.Failed;
            }

            LibassNative.AssSetFrameSize(renderer, 640, 360);
            LibassNative.AssSetFonts(renderer, null, inspected.FamilyName, 0, null, 0);
            byte[] script = Encoding.UTF8.GetBytes(BuildScript(inspected.FamilyName, scalar));
            fixed (byte* scriptData = script)
            {
                track = LibassNative.AssReadMemory(library, scriptData, checked((nuint)script.Length), "UTF-8");
            }

            return track != IntPtr.Zero && LibassNative.AssRenderFrame(renderer, track, 1000, out _) != IntPtr.Zero
                ? FontVerificationStatus.Passed
                : FontVerificationStatus.Failed;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return FontVerificationStatus.Unknown;
        }
        finally
        {
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

    private static bool CheckAvailability()
    {
        try
        {
            LibassNative.RegisterResolver();
            IntPtr library = LibassNative.AssLibraryInit();
            if (library == IntPtr.Zero)
            {
                return false;
            }

            LibassNative.AssLibraryDone(library);
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    private static bool IsPrintable(uint scalar)
    {
        if (scalar is < 33 or > 0x10FFFF || scalar is '\\' or '{' or '}' || scalar is >= 0xD800 and <= 0xDFFF)
        {
            return false;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(new Rune(checked((int)scalar)));
        return category is not UnicodeCategory.Control and not UnicodeCategory.Format
            and not UnicodeCategory.SpaceSeparator and not UnicodeCategory.LineSeparator
            and not UnicodeCategory.ParagraphSeparator and not UnicodeCategory.NonSpacingMark
            and not UnicodeCategory.EnclosingMark and not UnicodeCategory.Surrogate
            and not UnicodeCategory.PrivateUse and not UnicodeCategory.OtherNotAssigned;
    }

    private static string BuildScript(string family, uint scalar)
    {
        return "[Script Info]\nScriptType: v4.00+\nPlayResX: 640\nPlayResY: 360\n"
            + "[V4+ Styles]\n"
            + "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
            + "Style: Probe," + family + ",48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,5,10,10,10,1\n"
            + "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
            + "Dialogue: 0,0:00:00.00,0:00:02.00,Probe,,0,0,0,," + char.ConvertFromUtf32(checked((int)scalar)) + '\n';
    }
}
