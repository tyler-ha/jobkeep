using Jobkeep.SharedKernel;

namespace Jobkeep.Api.GraphQL;

// GraphQL's edge translation of a slice Result — the counterpart to REST's
// ToHttpResult. GraphQL has no status codes: a failure is an entry in the
// response's "errors" array, which is what HotChocolate builds from a thrown
// GraphQLException. Same handler, same Result object, two presentations.
//
// The `code` is what a client should branch on ("NOT_FOUND"), not the message.
public static class ResultExtensions
{
    public static T ValueOrThrow<T>(this SliceResult<T> result)
    {
        if (result.Status == ResultStatus.Ok) return result.Value!;

        throw new GraphQLException(ErrorBuilder.New()
            .SetMessage(result.Error ?? "Request failed.")
            .SetCode(result.Status == ResultStatus.NotFound ? "NOT_FOUND" : "INVALID_INPUT")
            .Build());
    }
}
