using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jobkeep.Tests.Infrastructure;

/// <summary>
/// Phase 11.2a — the suite's way past <c>[Authorize]</c>.
///
/// <para>
/// Every controller now refuses an anonymous caller, and 338 tests were written
/// before that was true. The alternative to this file is registering and signing
/// in a real user in each test's arrange: Respawn truncates the <c>identity</c>
/// schema between tests, so the account cannot be shared, and Identity hashes
/// passwords deliberately slowly — roughly a tenth of a second, paid three
/// hundred times, on a suite that runs in under a minute. It would double the
/// suite to re-prove something <c>IdentityTests</c> already proves once.
/// </para>
///
/// <para>
/// So the header <c>X-Test-User</c> carries a user id and this scheme turns it
/// into a <see cref="ClaimsPrincipal"/>. It is not a bypass of the authorization
/// rule — the request still has to satisfy it — only of the login round trip.
/// </para>
///
/// <para>
/// WHY IT FORWARDS INSTEAD OF REPLACING. This is registered as the default
/// scheme, which would otherwise make the cookie unreachable and quietly gut
/// <c>IdentityTests</c>: those assert that a real login leaves a real cookie and
/// that a request without one is a 401, and both would start passing for the
/// wrong reason. <c>ForwardDefaultSelector</c> hands every request with no test
/// header back to Identity's own default scheme, so the only requests this
/// handler ever sees are the ones that opted in.
/// </para>
///
/// <para>
/// The forward target is BearerAndApplicationScheme — Identity's composite, and
/// the DefaultScheme this registration displaces — NOT the application cookie.
/// Forwarding to the cookie alone looks equivalent and is not: challenged
/// directly, the cookie handler REDIRECTS to its LoginPath ("/Account/Login", a
/// Razor page this app does not have), where the composite forwards the
/// challenge to the bearer handler and answers 401. Getting that wrong makes the
/// suite see a 404 where production sends a 401 — a test double lying about the
/// thing it exists to stand in for, and it cost a wrong "fix" to Program.cs
/// before the cause was found.
/// </para>
///
/// <para>
/// It carries a NameIdentifier claim because that is what 11.2b reads the owner
/// out of. Two ids in two tests is then a second user, which is the assertion
/// that phase exists to make.
/// </para>
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";

    /// <summary>
    /// The user every test is by default. A constant rather than a fresh Guid per
    /// test: rows arranged through <c>WithDbAsync</c> have to end up owned by the
    /// same person the HTTP client is, and a random id would make that a thing
    /// each test had to remember.
    /// </summary>
    public static readonly Guid DefaultUserId = new("11111111-1111-1111-1111-111111111111");

    /// <summary>A second person, for the tests that need one. Never signed in by default.</summary>
    public static readonly Guid OtherUserId = new("22222222-2222-2222-2222-222222222222");

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Unreachable when the header is absent — the forwarding selector hands
        // those to Identity before this runs. Guarded anyway, because
        // "unreachable" here depends on a lambda thirty lines down.
        if (!Context.Request.Headers.TryGetValue(UserHeader, out var raw)
            || !Guid.TryParse(raw.ToString(), out var userId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, $"{userId}@test.local"),
            ],
            SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    /// <summary>
    /// Registration, kept beside the handler so the forwarding rule and the reason
    /// for it are one thing to read.
    /// </summary>
    public static void Register(IServiceCollection services)
    {
        // Read rather than retyped. The composite's name is
        // "Identity.BearerAndApplication", but IdentityConstants' field for it is
        // INTERNAL — so the only honest way to name it is to take the default the
        // app configured, in the last Configure delegate before it is replaced.
        var identityDefault = string.Empty;

        services
            .AddAuthentication(o =>
            {
                identityDefault = o.DefaultScheme ?? IdentityConstants.ApplicationScheme;
                o.DefaultScheme = SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(SchemeName, o =>
                o.ForwardDefaultSelector = ctx =>
                    ctx.Request.Headers.ContainsKey(UserHeader)
                        ? null
                        : identityDefault);
    }
}
