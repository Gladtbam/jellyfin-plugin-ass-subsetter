using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Contains one deterministic repository page and its exact matching total.</summary>
/// <typeparam name="T">The persisted record type.</typeparam>
/// <param name="Items">The page records in deterministic order.</param>
/// <param name="TotalCount">The exact number of matching persisted records.</param>
public sealed record FontRepositoryPage<T>(IReadOnlyList<T> Items, long TotalCount);
