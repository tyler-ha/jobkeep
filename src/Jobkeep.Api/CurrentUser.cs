using System.Security.Claims;
using Jobkeep.SharedKernel;

namespace Jobkeep.Api;

/// <summary>
/// Phase 11.2b — the request principal, reduced to the one fact the data layer
/// needs.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, so it is one value for the lifetime of a request and every context
/// resolved inside that request agrees about who is asking. Resolved LAZILY
/// rather than in the constructor: <c>ImportParseWorker</c> creates a scope with
/// no <c>HttpContext</c> and assigns the owner it read off the row before it
/// resolves anything else, and a constructor that had already looked would have
/// made that assignment a no-op in the confusing direction.
/// </para>
/// <para>
/// <c>NameIdentifier</c> is what ASP.NET Core Identity puts the user id in, and
/// it is what <c>TestAuthHandler</c> writes — the suite and production read the
/// same claim, which is the only reason the test double is worth having.
/// </para>
/// </remarks>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private Guid? _userId;
    private bool _resolved;

    public Guid? UserId
    {
        get
        {
            if (_resolved) return _userId;

            var raw = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            _userId = Guid.TryParse(raw, out var id) ? id : null;
            _resolved = true;
            return _userId;
        }
        set
        {
            _userId = value;
            _resolved = true;
        }
    }
}
