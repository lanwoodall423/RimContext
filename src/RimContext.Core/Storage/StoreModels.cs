namespace RimContext.Core.Storage;

public sealed record StoreMetadata(
    int SchemaVersion,
    string ToolVersion,
    string WorkspaceIdentity,
    string WorkspaceRoot,
    string ConfigurationFingerprint,
    string IndexedAtUtc);

public sealed record IndexedFileRecord(
    string Id,
    string Kind,
    string Path,
    string ContentHash,
    long SizeBytes,
    long ModifiedUtcTicks,
    string WorkspaceIdentity,
    string ParseStatus = "not_started",
    string? Diagnostic = null);

public sealed record EntityRecord(
    string Id,
    string Kind,
    string IdentityKey,
    string? FileId,
    int? Line,
    string PayloadJson);

public sealed record RelationRecord(
    string Id,
    string FromId,
    string? ToId,
    string Kind,
    string? FileId,
    int? Line,
    string PayloadJson);

public sealed record IndexCounts(long FileCount, long EntityCount, long RelationCount);

public sealed record IndexStatistics(
    int Scanned,
    int Added,
    int Changed,
    int Removed,
    int Unchanged);

public sealed record IndexDiagnostic(
    string Path,
    string Message,
    string Code = "INDEX");

public sealed record IndexBuildResult(
    StoreMetadata Metadata,
    IndexCounts Counts,
    int DiscoveredFileCount,
    IndexStatistics Statistics,
    long DurationMilliseconds,
    IReadOnlyList<IndexDiagnostic>? Diagnostics = null);
