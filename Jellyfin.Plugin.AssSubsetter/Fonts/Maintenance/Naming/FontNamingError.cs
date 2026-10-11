namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

/// <summary>A syntax or type error positioned in the original input.</summary>
/// <param name="Field">The configuration field.</param>
/// <param name="Code">The stable error code.</param>
/// <param name="Offset">The zero-based character offset.</param>
public sealed record FontNamingError(string Field, string Code, int Offset);
