using System;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Describes one durable quarantine item without accepting a client-side path.</summary>
/// <param name="Id">The persisted item identifier.</param>
/// <param name="MaintenanceBatchId">The owning maintenance batch identifier used for restore requests.</param>
/// <param name="FileId">The persisted physical-file identifier.</param>
/// <param name="Status">The item lifecycle state.</param>
/// <param name="SourcePath">The original font path, for administrator review only.</param>
/// <param name="QuarantinePath">The generated quarantine path, for administrator review only.</param>
/// <param name="QuarantinedUtc">The successful quarantine time.</param>
/// <param name="ExpiresUtc">The fixed expiry time.</param>
/// <param name="RestoredUtc">The successful restore time.</param>
/// <param name="Error">The safe durable item diagnostic.</param>
public sealed record QuarantineItemDto(long Id, long MaintenanceBatchId, long FileId, QuarantineItemStatus Status, string SourcePath, string QuarantinePath, DateTimeOffset? QuarantinedUtc, DateTimeOffset? ExpiresUtc, DateTimeOffset? RestoredUtc, string? Error);
