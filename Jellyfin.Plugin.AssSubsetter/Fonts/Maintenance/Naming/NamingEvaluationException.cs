using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

/// <summary>A required naming field or result is unavailable.</summary>
/// <param name="reason">The failure with field provenance.</param>
public sealed class NamingEvaluationException(string reason) : Exception(reason);
