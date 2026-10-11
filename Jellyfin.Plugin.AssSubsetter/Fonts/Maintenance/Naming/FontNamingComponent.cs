using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

internal static class FontNamingComponent
{
    internal static string Normalize(string value)
    {
        for (int position = 0; position < value.Length;)
        {
            if (!Rune.TryGetRuneAt(value, position, out Rune rune))
            {
                throw new NamingEvaluationException("invalid-unicode");
            }

            position += rune.Utf16SequenceLength;
        }

        string normalized = value.Trim().Normalize(NormalizationForm.FormC);
        return IsSafe(normalized) ? normalized : FilenameSanitizer.Sanitize(normalized);
    }

    internal static bool IsSafe(string value)
        => !string.IsNullOrWhiteSpace(value) && value is not "." and not ".." && !value.EndsWith(' ') && !value.EndsWith('.')
            && !Regex.IsMatch(value, "[<>:\"/\\\\|?*\\p{Cc}]", RegexOptions.CultureInvariant)
            && !Regex.IsMatch(value.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
