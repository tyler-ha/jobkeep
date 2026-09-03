namespace Jobkeep.SharedKernel;

// What a slice handler returns. A handler knows whether the thing it was asked
// to do succeeded, wasn't found, or was invalid — it does NOT know whether it
// was called over REST or GraphQL, so it must not return a status code or throw
// an HTTP-shaped exception.
//
// Each surface translates this at its own edge: ToHttpResult (ResultHttp-
// Extensions.cs) for REST, ValueOrThrow (GraphQL/ResultExtensions.cs) for
// GraphQL. That split is what makes "one rule, one implementation" true in
// practice — the rule is decided once inside the handler, and only its
// *presentation* differs per surface.
//
// Named SliceResult rather than the obvious `Result` because HotChocolate's
// DataLoader library (GreenDonut) publishes its own `Result<T>` through a global
// using, and the collision is a compile error in every slice file. Renaming ours
// is cheaper and clearer than qualifying every usage.
public enum ResultStatus
{
    Ok,
    NotFound,
    Invalid
}

public sealed class SliceResult<T>
{
    private SliceResult(ResultStatus status, T? value, string? error)
    {
        Status = status;
        Value = value;
        Error = error;
    }

    public ResultStatus Status { get; }
    public T? Value { get; }
    public string? Error { get; }

    public static SliceResult<T> Ok(T value) => new(ResultStatus.Ok, value, null);

    // The addressed row doesn't exist — or exists but isn't reachable from the
    // application in the route, which is the same thing from the caller's side.
    public static SliceResult<T> NotFound(string error) => new(ResultStatus.NotFound, default, error);

    // The request itself breaks a rule the handler enforces, before the DB is asked.
    public static SliceResult<T> Invalid(string error) => new(ResultStatus.Invalid, default, error);
}
