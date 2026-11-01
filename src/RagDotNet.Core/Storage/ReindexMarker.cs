namespace RagDotNet.Storage;

/// <summary>Records that a reindex to <paramref name="TargetModelId"/> is in progress, owned by <paramref name="OwnerId"/>.</summary>
/// <param name="TargetModelId">The model the reindex re-embeds into.</param>
/// <param name="OwnerId">The engine instance running the reindex.</param>
/// <param name="HeartbeatUnixMs">When the owner last showed it is alive, in Unix milliseconds.</param>
internal sealed record ReindexMarker(string TargetModelId, string OwnerId, long HeartbeatUnixMs);
