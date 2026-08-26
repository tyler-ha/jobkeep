namespace Jobkeep.Models;

// Phase 2.5. The status lifecycle, as a pure function of (from, to).
//
// It lives in Models/ rather than in the update slice deliberately: this is a
// statement about the domain, not about PATCH. A pure static rule with no
// AppDbContext in it is testable without a database — the one place in this
// project where a plain unit test beats an integration test, because there is
// no SQL, no mapping and no surface for an integration test to catch anything
// in. Everything else here goes through the real HTTP surface.
//
// The table below is a *business* decision, confirmed with the user rather than
// inferred. Two things about its shape are worth being able to defend:
//
//   1. Applied -> Offer is legal. An offer can arrive without an interview stage
//      ever being logged — a referral, a contract role, or simply forgetting to
//      move the card. Forbidding it would make the tracker argue with reality.
//
//   2. Rejected and Withdrawn are *closed*, not terminal. Huntr (a drag-and-drop
//      Kanban board) and Teal (a status dropdown) both let you move a job out of
//      a closed stage, and neither treats it as a one-way door. Following that:
//      a closed application can be re-opened or re-labelled, but an *Offer* can
//      only be reached from an active one — you cannot conjure an offer out of a
//      rejection.
//
// What is still forbidden, and is the actual invariant this buys:
//   Interviewing -> Applied, Offer -> Applied, Offer -> Interviewing
//     — an active application does not walk backwards down the pipeline.
//   Rejected -> Offer, Withdrawn -> Offer
//     — see (2).
//
// Deliberately NOT here: status *history*. Recording every transition and when
// it happened is a new table and its own phase; this enforces the rule on the
// single current Status field. See docs/phases/phase-2.5-status-rules.md.
public static class ApplicationStatusTransitions
{
    private static readonly IReadOnlyDictionary<ApplicationStatus, ApplicationStatus[]> Allowed =
        new Dictionary<ApplicationStatus, ApplicationStatus[]>
        {
            [ApplicationStatus.Applied] = new[]
            {
                ApplicationStatus.Interviewing,
                ApplicationStatus.Offer,
                ApplicationStatus.Rejected,
                ApplicationStatus.Withdrawn,
            },
            [ApplicationStatus.Interviewing] = new[]
            {
                ApplicationStatus.Offer,
                ApplicationStatus.Rejected,
                ApplicationStatus.Withdrawn,
            },
            [ApplicationStatus.Offer] = new[]
            {
                ApplicationStatus.Rejected,
                ApplicationStatus.Withdrawn,
            },
            [ApplicationStatus.Rejected] = new[]
            {
                ApplicationStatus.Applied,
                ApplicationStatus.Interviewing,
                ApplicationStatus.Withdrawn,
            },
            [ApplicationStatus.Withdrawn] = new[]
            {
                ApplicationStatus.Applied,
                ApplicationStatus.Interviewing,
                ApplicationStatus.Rejected,
            },
        };

    /// <summary>
    /// Is moving an application from <paramref name="from"/> to <paramref name="to"/>
    /// legal? Staying put is always legal — a PATCH that re-sends the current status,
    /// or that never mentions status at all, is not a transition.
    /// </summary>
    public static bool IsAllowed(ApplicationStatus from, ApplicationStatus to)
        => from == to || Allowed[from].Contains(to);

    /// <summary>
    /// The message a caller sees when <see cref="IsAllowed"/> says no. Built here so
    /// REST and GraphQL cannot word the same refusal differently.
    /// </summary>
    public static string RejectionMessage(ApplicationStatus from, ApplicationStatus to)
        => $"Cannot move from {from} to {to}.";
}
