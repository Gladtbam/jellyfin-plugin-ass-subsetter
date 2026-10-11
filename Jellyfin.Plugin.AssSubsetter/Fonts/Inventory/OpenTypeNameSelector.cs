using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>Selects one decoded OpenType name consistently for inventory, analysis, and filename planning.</summary>
internal static class OpenTypeNameSelector
{
    public static string? SelectPreferred(IEnumerable<OpenTypeNameRecord> records)
    {
        return records
            .Where(record => !string.IsNullOrWhiteSpace(record.Value))
            .OrderByDescending(record => record.PlatformId == 3 && record.LanguageId == 0x0409)
            .ThenByDescending(record => record.PlatformId is 0 or 3)
            .ThenBy(record => record.LanguageId)
            .ThenBy(record => record.Value, StringComparer.Ordinal)
            .Select(record => record.Value.Trim())
            .FirstOrDefault();
    }
}
