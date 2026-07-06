namespace Jellyfin.Plugin.AssSubsetter.Models;

/// <summary>
/// Uniquely identifies a requested font variant, including weight and italic properties.
/// </summary>
/// <param name="FontName">The family name of the font.</param>
/// <param name="RequestedWeight">The exact weight requested (e.g., 700), or null if not specifically requested.</param>
/// <param name="IsBoldRequest">True if a generic bold (-1 or {\b1}) was requested.</param>
/// <param name="IsItalic">True if italic was requested.</param>
public record FontDescriptor(string FontName, int? RequestedWeight, bool IsBoldRequest, bool IsItalic);
