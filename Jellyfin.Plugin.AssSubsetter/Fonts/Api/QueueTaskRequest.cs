namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Identifies an idempotent task-only operation.</summary>
/// <param name="IdempotencyKey">The administrator-generated retry key.</param>
public sealed record QueueTaskRequest(string IdempotencyKey);
