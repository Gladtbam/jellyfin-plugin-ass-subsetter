using DomainPolicy = Jellyfin.Plugin.AssSubsetter.Fonts.Domain.FontDirectoryPolicy;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>Describes one normalized inventory root and its direct policy flags.</summary>
/// <param name="Path">The normalized root path.</param>
/// <param name="Policy">The configured safety and preference flags.</param>
public sealed record FontDirectoryRoot(string Path, DomainPolicy Policy);
