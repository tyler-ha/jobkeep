using Jobkeep.SharedKernel;
namespace Jobkeep.Api;

// The REST edge's translation of a SliceResult into a status code. Lives here
// rather than inside each endpoint so the mapping (NotFound -> 404, Invalid ->
// 400) is decided once; an endpoint that wants a different success code — 201,
// 204 — passes onOk rather than re-deciding the failure cases.
public static class ResultHttpExtensions
{
    public static IResult ToHttpResult<T>(this SliceResult<T> result, Func<T, IResult>? onOk = null) =>
        result.Status switch
        {
            ResultStatus.Ok => onOk is null ? Results.Ok(result.Value) : onOk(result.Value!),
            ResultStatus.NotFound => Results.NotFound(result.Error),
            _ => Results.BadRequest(result.Error)
        };
}
