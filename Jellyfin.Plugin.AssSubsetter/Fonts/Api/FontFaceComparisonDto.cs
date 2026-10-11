namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Identifies one side of a font pair using persisted inventory fields.</summary>
public sealed record FontFaceComparisonDto(
    long FaceId,
    string Path,
    string Family,
    string Style,
    int Weight,
    int Width,
    bool Italic,
    string? PostScriptName,
    string? Version,
    string StrictFingerprint);
