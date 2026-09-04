using Microsoft.AspNetCore.Identity;

namespace Jobkeep.Modules.Identity.Domain;

// The account. Phase 11.1a.
//
// EMPTY ON PURPOSE, and that is the whole argument for taking the platform's
// identity system rather than writing a users table: IdentityUser<Guid> already
// carries the email, the normalised email, the password hash, the security
// stamp, the concurrency stamp, the lockout window, the failed-attempt count and
// the 2FA flag — nine columns nobody here has to get right. A hand-rolled users
// table would have been the same nine columns written by someone learning which
// ones matter.
//
// GUID KEY, not Identity's default string. IdentityUser's TKey defaults to
// string and stores a GUID's text form, which would make this the only table in
// the database whose primary key is text — and, more to the point, would make
// 11.2's OwnerUserId a varchar foreign key on every scoped table. Guid matches
// every other id here and matches what ApplyDatabaseDefaults already knows how
// to default.
//
// NOTHING IS ADDED YET. A display name, a timezone or a target-role profile are
// all plausible and none is needed to log in, so they wait for a screen that
// shows them. The subclass exists at all because Identity's generic machinery is
// typed on the user, so introducing it later would touch every one of those
// generic parameters; introducing it now costs one empty class.
public class JobkeepUser : IdentityUser<Guid>;
