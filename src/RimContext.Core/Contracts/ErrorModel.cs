namespace RimContext.Core.Contracts;

public static class ErrorCodes
{
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string LimitExceeded = "LIMIT_EXCEEDED";
    public const string AmbiguousEntity = "AMBIGUOUS_ENTITY";
    public const string NotFound = "NOT_FOUND";
    public const string IndexNotFound = "INDEX_NOT_FOUND";
    public const string IndexIncompatible = "INDEX_INCOMPATIBLE";
    public const string RootMismatch = "ROOT_MISMATCH";
    public const string PathNotFound = "PATH_NOT_FOUND";
    public const string InputReadFailed = "INPUT_READ_FAILED";
    public const string StoreLocked = "STORE_LOCKED";
    public const string StoreFailed = "STORE_FAILED";
    public const string IndexFailed = "INDEX_FAILED";
    public const string NotImplemented = "not_implemented";
    public const string Internal = "INTERNAL";
}

public sealed record RimContextError(
    string Code,
    string Message,
    string? Path = null,
    object? Details = null);

public sealed class RimContextException : Exception
{
    public RimContextException(string code, string message, int exitCode, string? path = null, object? details = null)
        : base(message)
    {
        Error = new RimContextError(code, message, path, details);
        ExitCode = exitCode;
    }

    public RimContextError Error { get; }

    public int ExitCode { get; }
}

public static class ErrorFactory
{
    public static RimContextException InvalidArgument(string message, object? details = null) =>
        new(ErrorCodes.InvalidArgument, message, 2, details: details);

    public static RimContextException LimitExceeded(string message) =>
        new(ErrorCodes.LimitExceeded, message, 2);

    public static RimContextException AmbiguousEntity(string message, object? details = null) =>
        new(ErrorCodes.AmbiguousEntity, message, 2, details: details);

    public static RimContextException NotFound(string selector) =>
        new(ErrorCodes.NotFound, $"{selector} not found", 4);

    public static RimContextException IndexNotFound() =>
        new(ErrorCodes.IndexNotFound, "No RimContext index exists for the selected root.", 3);

    public static RimContextException IndexIncompatible(string message, object? details = null) =>
        new(ErrorCodes.IndexIncompatible, message, 3, details: details);

    public static RimContextException RootMismatch(string message, object? details = null) =>
        new(ErrorCodes.RootMismatch, message, 3, details: details);

    public static RimContextException PathNotFound() =>
        new(ErrorCodes.PathNotFound, "The selected path does not exist.", 4);

    public static RimContextException InputReadFailed(string? path, string message) =>
        new(ErrorCodes.InputReadFailed, message, 4, path);

    public static RimContextException StoreLocked() =>
        new(ErrorCodes.StoreLocked, "The index store is locked by another operation.", 4);

    public static RimContextException StoreFailed(string message, string? path = null) =>
        new(ErrorCodes.StoreFailed, message, 4, path);

    public static RimContextException IndexFailed(string message, object? details = null) =>
        new(ErrorCodes.IndexFailed, message, 5, details: details);

    public static RimContextException NotImplemented(string command) =>
        new(ErrorCodes.NotImplemented, $"The '{command}' query is not implemented in this foundation.", 6);

    public static RimContextException Internal(string message) =>
        new(ErrorCodes.Internal, message, 10);
}
