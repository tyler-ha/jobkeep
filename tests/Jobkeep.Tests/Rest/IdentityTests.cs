using System.Net;
using System.Net.Http.Json;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Rest;

/// <summary>
/// Phase 11.1b — register and sign in.
///
/// <para>
/// The endpoints are the framework's (<c>MapIdentityApi</c>), so these are not
/// tests of password hashing or of the security stamp — those are already tested,
/// which is the entire reason decision 3 chose the package. What is untested by
/// Microsoft, and is this app's own, is the WIRING: a Guid-keyed user against a
/// context in the `identity` schema, reached through a cookie that a cross-origin
/// browser is allowed to send. Every assertion below is about one of those.
/// </para>
///
/// <para>
/// The client handles cookies (<c>WebApplicationFactoryClientOptions.HandleCookies</c>
/// defaults to true), so a successful login here leaves the same state a browser
/// would be left in — which is what makes the round trip meaningful rather than a
/// re-read of the response body.
/// </para>
/// </summary>
public sealed class IdentityTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    // Satisfies Identity's default password policy — digit, upper, lower,
    // non-alphanumeric, six characters. Left at the defaults deliberately: a
    // relaxed policy is a decision, and this phase had no reason to make one.
    //
    // GENERATED, not a literal, and not for cosmetic reasons: a password-shaped
    // constant trips the repo's secret scanner on every PR that touches this
    // file, and a scanner people learn to click past is worse than no scanner.
    // One value per run, reused across the tests in it.
    private static readonly string Password = $"Aa1!{Guid.NewGuid():N}";

    private Task<HttpResponseMessage> RegisterAsync(string email) =>
        Client.PostAsJsonAsync("/identity/register", new { email, password = Password }, Ct);

    private Task<HttpResponseMessage> LoginAsync(string email) =>
        Client.PostAsJsonAsync("/identity/login?useCookies=true", new { email, password = Password }, Ct);

    [Fact]
    public async Task Register_ThenLogin_LeavesTheCallerAuthenticated()
    {
        const string Email = "tyler@example.com";

        var registered = await RegisterAsync(Email);
        var loggedIn = await LoginAsync(Email);
        var whoAmI = await Client.GetAsync("/identity/manage/info", Ct);

        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        Assert.Equal(HttpStatusCode.OK, loggedIn.StatusCode);

        // The round trip, and the point of the test: no token was read out of the
        // login response and put anywhere. The cookie the browser would hold is
        // the only thing carrying identity into this third request.
        Assert.Equal(HttpStatusCode.OK, whoAmI.StatusCode);
        Assert.Contains(Email, await whoAmI.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ManageInfo_WithoutSigningIn_Is401()
    {
        var response = await Client.GetAsync("/identity/manage/info", Ct);

        // A 404 here would mean the endpoints are not mapped; a 200 would mean the
        // authentication middleware is not in the pipeline. Both have happened to
        // people, and both look identical from a green suite otherwise.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_ClearsTheCookie()
    {
        const string Email = "logout@example.com";
        await RegisterAsync(Email);
        await LoginAsync(Email);

        var loggedOut = await Client.PostAsync("/identity/logout", content: null, Ct);
        var after = await Client.GetAsync("/identity/manage/info", Ct);

        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Register_WithAnEmailAlreadyTaken_IsRefused()
    {
        const string Email = "twice@example.com";
        await RegisterAsync(Email);

        var second = await RegisterAsync(Email);

        // Identity's own uniqueness, on NormalizedUserName — worth pinning because
        // 11.1a skipped ApplyDatabaseDefaults on this context, and a convention
        // skipped is a place where something can quietly not be there.
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task TheUserRow_LandsInTheIdentitySchema_WithAGuidKey()
    {
        await RegisterAsync("schema@example.com");

        // pg_typeof over the raw column: the whole reason JobkeepUser is
        // IdentityUser<Guid> rather than the default IdentityUser is that the
        // default stores the key as text, which would make 11.2's OwnerUserId a
        // varchar foreign key on every scoped table. Reading it back through EF
        // would prove nothing — the CLR type is Guid either way.
        var keyType = await ScalarAsync(
            """select pg_typeof("Id")::text from identity."AspNetUsers" limit 1""");

        Assert.Equal("uuid", keyType);
    }
}
