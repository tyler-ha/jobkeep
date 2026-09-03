using System.Net.Http.Json;
using System.Text.Json;
using Jobkeep.Api.GraphQL;
using Jobkeep.SharedKernel;

namespace Jobkeep.Tests.Infrastructure;

/// <summary>A GraphQL response split into the two halves tests care about.</summary>
public sealed record GraphQLResponse(JsonElement? Data, JsonElement[] Errors)
{
    public bool HasErrors => Errors.Length > 0;

    /// <summary>
    /// The <c>extensions.code</c> of the first error — "NOT_FOUND" or "INVALID_INPUT",
    /// set by GraphQL/ResultExtensions.cs when it translates a SliceResult. This is the
    /// GraphQL half of every parity assertion.
    /// </summary>
    public string? FirstErrorCode =>
        Errors.Length == 0 ? null
        : Errors[0].TryGetProperty("extensions", out var ext) && ext.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;

    public string? FirstErrorMessage =>
        Errors.Length == 0 ? null
        : Errors[0].TryGetProperty("message", out var m) ? m.GetString() : null;
}

/// <summary>
/// Minimal POST /graphql helper. Deliberately hand-rolled rather than a generated
/// client: the tests assert on raw shape, including error codes, and a strongly typed
/// client would hide exactly the surface differences the parity tests exist to catch.
/// </summary>
public sealed class GraphQLClient(HttpClient client)
{
    public async Task<GraphQLResponse> QueryAsync(string query, object? variables = null)
    {
        var response = await client.PostAsJsonAsync("/graphql", new { query, variables });
        var body = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var errors = root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array
            ? errs.EnumerateArray().Select(e => e.Clone()).ToArray()
            : [];

        JsonElement? data = root.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null
            ? d.Clone()
            : null;

        return new GraphQLResponse(data, errors);
    }
}
