namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Describes the fixed 40/25/20/10/5 retention scoring dimensions.</summary>
/// <param name="Content">The content-completeness contribution, capped at forty points.</param>
/// <param name="Version">The twenty-five-point version contribution.</param>
/// <param name="Technical">The twenty-point technical-integrity contribution.</param>
/// <param name="Names">The ten-point name-completeness contribution.</param>
/// <param name="Modified">The five-point modification-time contribution.</param>
public sealed record RetentionScoreBreakdownDto(int Content, int Version, int Technical, int Names, int Modified);
