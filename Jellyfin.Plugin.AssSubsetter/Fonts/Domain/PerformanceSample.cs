using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Local performance measurement produced by inventory or analysis phases.
/// </summary>
public sealed record PerformanceSample
{
    /// <summary>Gets the associated inventory generation when applicable.</summary>
    public long? GenerationId { get; init; }

    /// <summary>Gets the measured phase name.</summary>
    public required string Phase { get; init; }

    /// <summary>Gets the measured duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Gets the number of processed items.</summary>
    public long ItemCount { get; init; }

    /// <summary>Gets the number of reused items.</summary>
    public long ReusedCount { get; init; }

    /// <summary>Gets the sample timestamp.</summary>
    public DateTimeOffset RecordedUtc { get; init; }
}
