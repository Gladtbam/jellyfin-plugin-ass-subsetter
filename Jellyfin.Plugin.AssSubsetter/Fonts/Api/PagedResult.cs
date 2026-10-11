using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Represents one bounded page of administrator-facing persisted records.</summary>
/// <typeparam name="T">The record type returned by the page.</typeparam>
/// <param name="Items">The records in the requested page.</param>
/// <param name="Page">The one-based page number.</param>
/// <param name="PageSize">The clamped page size.</param>
/// <param name="TotalCount">The total matching record count.</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);
