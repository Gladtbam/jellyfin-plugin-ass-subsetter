using System;
using System.Text;

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
            && !FilenameSanitizer.InvalidFilenameCharactersRegex().IsMatch(value)
            && !FilenameSanitizer.ReservedDeviceNameRegex().IsMatch(value.Split('.')[0]);
}
