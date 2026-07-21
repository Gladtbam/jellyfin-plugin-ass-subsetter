using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
/// Resolves the external ASS subtitle stream explicitly requested by a Jellyfin route.
/// </summary>
public sealed class SubtitleStreamResolver
{
    private readonly IMediaSourceManager _mediaSourceManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleStreamResolver"/> class.
    /// </summary>
    /// <param name="mediaSourceManager">The Jellyfin media source manager.</param>
    public SubtitleStreamResolver(IMediaSourceManager mediaSourceManager)
    {
        _mediaSourceManager = mediaSourceManager;
    }

    /// <summary>
    /// Resolves an existing external ASS path by media source and stream index.
    /// </summary>
    /// <param name="video">The requested video.</param>
    /// <param name="mediaSourceId">The requested media source identifier.</param>
    /// <param name="subtitleIndex">The requested subtitle stream index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resolved ASS path, or <see langword="null"/> when the requested stream is not usable.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "The path comes from the requested media stream stored in Jellyfin's media source model.")]
    public async Task<string?> ResolveExternalAssPathAsync(
        Video video,
        string mediaSourceId,
        int subtitleIndex,
        CancellationToken cancellationToken)
    {
        var mediaSource = await _mediaSourceManager.GetMediaSource(
            video,
            mediaSourceId,
            null,
            false,
            cancellationToken).ConfigureAwait(false);
        var subtitleStream = mediaSource.MediaStreams.FirstOrDefault(stream => stream.Type == MediaStreamType.Subtitle && stream.Index == subtitleIndex);
        string? path = subtitleStream?.Path;

        return subtitleStream?.IsExternal == true
            && !string.IsNullOrWhiteSpace(path)
            && string.Equals(Path.GetExtension(path), ".ass", StringComparison.OrdinalIgnoreCase)
            && File.Exists(path)
                ? path
                : null;
    }
}
