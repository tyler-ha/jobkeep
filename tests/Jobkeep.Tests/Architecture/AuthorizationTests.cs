using System.Net;
using Jobkeep.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Tests.Architecture;

/// <summary>
/// Phase 11.2a — nothing but <c>/identity</c> is reachable without signing in.
///
/// <para>
/// The first test is the one that matters, and it is deliberately not "five
/// controllers carry <c>[Authorize]</c>". It reads the app's own
/// <see cref="EndpointDataSource"/> — the routing table ASP.NET actually built —
/// so a sixth controller, a hand-written minimal API or a second GraphQL mount
/// are all covered by it on the day they are added, without this file being
/// touched. F1 is an open door, and a test that only inspects the doors it was
/// told about is not a lock.
/// </para>
///
/// <para>
/// The two that follow are the same question asked through the wire, once per
/// surface. They exist because the metadata check would still pass if
/// <c>UseAuthorization()</c> were deleted from the pipeline: metadata is a
/// declaration, and only a request proves anything enforces it. The GraphQL half
/// is the point of doing both — F5 was a GraphQL-only exposure that REST never
/// had.
/// </para>
/// </summary>
public sealed class AuthorizationTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    /// <summary>
    /// The sign-in surface itself, which cannot require a sign-in. <c>logout</c>
    /// lives under the same prefix and does carry authorization; this list is
    /// about what is ALLOWED to be open, not what is.
    /// </summary>
    private const string OpenPrefix = "identity/";

    [Fact]
    public void Every_endpoint_outside_identity_requires_authorization()
    {
        var endpoints = Fixture.App.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => !e.RoutePattern.RawText!.TrimStart('/').StartsWith(OpenPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A guard against the guard: if routing ever stops surfacing endpoints
        // here, every assertion below passes vacuously.
        Assert.NotEmpty(endpoints);

        var open = endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(e => $"{string.Join('/', e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])} /{e.RoutePattern.RawText!.TrimStart('/')}")
            .Order()
            .ToList();

        Assert.True(
            open.Count == 0,
            $"Reachable without authentication:{Environment.NewLine}{string.Join(Environment.NewLine, open)}");
    }

    [Fact]
    public async Task Rest_refuses_an_anonymous_caller()
    {
        using var anonymous = Fixture.App.CreateClient();

        var response = await anonymous.GetAsync("/applications", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GraphQL_refuses_an_anonymous_caller()
    {
        using var anonymous = Fixture.App.CreateClient();

        // Sent by hand rather than through GraphQLClient: that helper asserts a
        // successful envelope on the way past, and the answer here is an HTTP
        // status with no GraphQL envelope at all. Endpoint authorization refuses
        // the request before HotChocolate parses it — which is exactly the
        // property being asserted, since a resolver-level rule would have had to
        // let the document through first.
        var response = await anonymous.PostAsync(
            "/graphql",
            new StringContent("""{"query":"{ applications { items { id } } }"}""",
                System.Text.Encoding.UTF8,
                "application/json"),
            Ct);

        // 401, not a redirect. Identity's default scheme is a COMPOSITE that
        // forwards an unmet challenge to the bearer handler, which answers 401;
        // the application cookie on its own would have redirected to
        // "/Account/Login", a Razor page this app does not have. Asserting the
        // status code asserts that too — the redirect arrives here as a 404.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
