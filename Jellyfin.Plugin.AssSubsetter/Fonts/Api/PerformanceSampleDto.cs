using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Describes a persisted local performance sample.</summary>
/// <param name="GenerationId">The associated inventory generation, if any.</param>
/// <param name="Phase">The measured phase.</param>
/// <param name="Duration">The measured duration.</param>
/// <param name="ItemCount">The processed item count.</param>
/// <param name="ReusedCount">The reused item count.</param>
/// <param name="RecordedUtc">The sample timestamp.</param>
public sealed record PerformanceSampleDto(long? GenerationId, string Phase, TimeSpan Duration, long ItemCount, long ReusedCount, DateTimeOffset RecordedUtc);
