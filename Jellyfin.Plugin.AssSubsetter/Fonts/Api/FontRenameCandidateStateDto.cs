using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Observable background candidate generation state.</summary>
/// <param name="Status">pending, running, ready, stale or failed.</param>
/// <param name="GeneratedUtc">The last completed calculation time.</param>
/// <param name="OperationRequestId">The calculation root request.</param>
/// <param name="Error">The concrete generation error, when present.</param>
public sealed record FontRenameCandidateStateDto(string Status, DateTimeOffset? GeneratedUtc, long? OperationRequestId, string? Error);
