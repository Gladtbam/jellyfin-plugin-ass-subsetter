namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
/// Represents the result of a subtitle retrieval or generation operation.
/// </summary>
/// <param name="Path">The file path to the subtitle to serve.</param>
/// <param name="ContentType">The HTTP content type for the subtitle.</param>
/// <param name="IsReady">True if the target format is ready; false if serving a fallback.</param>
public record SubtitleResult(string Path, string ContentType, bool IsReady);
