namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>A text placeholder supported by the naming compiler and settings page.</summary>
/// <param name="Name">The case-sensitive field name.</param>
/// <param name="MissingBehavior">The fixed policy for unavailable values.</param>
/// <param name="IsSuffix">Whether the field owns its separator.</param>
public sealed record FontNamingFieldDescriptor(string Name, string MissingBehavior, bool IsSuffix);
