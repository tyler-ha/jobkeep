using System.Net;
using Jobkeep.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Jobkeep.Tests.Rest;

/// <summary>
/// The CORS policy added in Phase 6 step 6.1, so the front end's dev server can
/// call the API at all.
///
/// <para>
/// Worth a test for the same reason the Swagger document got one: this is
/// configuration whose failure mode appears somewhere else entirely. A missing or
/// narrowed policy does not break a route — every request here still answers 200,
/// and every assertion in the rest of the suite still passes. It breaks the
/// browser, in a different process, with a message that reads like a front-end
/// bug. Nothing in a green backend suite would notice.
/// </para>
///
/// <para>
/// The negative case is the more important one. An <c>AllowAnyOrigin</c> policy
/// would pass the first test and fail the second, and "temporary" wildcards are
/// exactly the ones that reach production — so the assertion that an unlisted
/// origin gets <em>nothing</em> back is what pins the decision rather than the
/// convenience.
/// </para>
/// </summary>
public class CorsTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string DevServer = "http://localhost:5173";

    private static HttpRequestMessage Preflight(string origin, string path = "/resumes")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        return request;
    }

    /// <summary>
    /// The origin in <c>appsettings.Development.json</c>, asserted against the real
    /// config file rather than an override — so the thing under test is the value
    /// the app actually ships with, not one the test invented.
    /// </summary>
    [Fact]
    public async Task Preflight_FromTheDevServer_IsAllowed()
    {
        var response = await Client.SendAsync(Preflight(DevServer), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(DevServer, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Preflight_FromAnUnlistedOrigin_GetsNoAllowHeader()
    {
        var response = await Client.SendAsync(Preflight("https://evil.example"), Ct);

        Assert.False(
            response.Headers.Contains("Access-Control-Allow-Origin"),
            "An unlisted origin was allowed, which means the policy is a wildcard.");
    }

    /// <summary>
    /// A real GET, not just the preflight — the header has to be on the actual
    /// response too, or the browser discards a body the server was happy to send.
    /// </summary>
    [Fact]
    public async Task ActualRequest_CarriesTheAllowOriginHeader()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/resumes");
        request.Headers.Add("Origin", DevServer);

        var response = await Client.SendAsync(request, Ct);

        response.EnsureSuccessStatusCode();
        Assert.Equal(DevServer, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    /// <summary>
    /// Phase 11.1b. Identity's default is a cookie, and a browser will not attach one
    /// to a cross-origin fetch unless the response says so — so without this header
    /// the front end would sign in successfully and be anonymous on the next request,
    /// with nothing wrong on the server side to find.
    /// </summary>
    [Fact]
    public async Task ActualRequest_AllowsCredentials_SoTheAuthCookieIsSent()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/resumes");
        request.Headers.Add("Origin", DevServer);

        var response = await Client.SendAsync(request, Ct);

        Assert.Equal(
            "true",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
    }

    /// <summary>
    /// The origins come from config, which is the point — a deployed front end lists
    /// its own origin without a code change, and step 6.2 can move the dev port if
    /// the build tool it picks serves on a different one.
    /// </summary>
    [Fact]
    public async Task AllowedOrigins_ComeFromConfiguration()
    {
        var app = Fixture.App.WithWebHostBuilder(builder =>
            builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:4321"));
        using var client = app.CreateClient();

        var configured = await client.SendAsync(Preflight("http://localhost:4321"), Ct);
        var previousDefault = await client.SendAsync(Preflight(DevServer), Ct);

        Assert.True(configured.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(
            previousDefault.Headers.Contains("Access-Control-Allow-Origin"),
            "Config replaced the origin list, so the built-in default should no longer be allowed.");
    }
}
