namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Explicitly confirms selection of every currently verified quarantine candidate in one analysis batch.</summary>
/// <param name="BatchId">The persisted current analysis batch identifier.</param>
/// <param name="ConfirmAllVerified">Whether the administrator explicitly confirms all verified candidates.</param>
/// <param name="IdempotencyKey">The administrator-generated retry key.</param>
public sealed record QueueAllVerifiedQuarantineRequest(long BatchId, bool ConfirmAllVerified, string IdempotencyKey);
