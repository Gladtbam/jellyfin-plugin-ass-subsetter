namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// One decoded OpenType name record with its identity coordinates preserved.
/// </summary>
/// <param name="PlatformId">The source platform identifier.</param>
/// <param name="EncodingId">The source encoding identifier.</param>
/// <param name="LanguageId">The source language identifier.</param>
/// <param name="NameId">The OpenType name identifier.</param>
/// <param name="Value">The decoded, trimmed name value.</param>
public sealed record OpenTypeNameRecord(
    ushort PlatformId,
    ushort EncodingId,
    ushort LanguageId,
    ushort NameId,
    string Value);
