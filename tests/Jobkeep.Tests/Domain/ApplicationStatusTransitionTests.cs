using Jobkeep.Models;

namespace Jobkeep.Tests.Domain;

/// <summary>
/// Phase 2.5. The only unit tests in the suite, and the exception proves the rule:
/// everything else here goes through the real HTTP surface against real Postgres,
/// because the bugs this project actually has (SQL that will not translate, delete
/// behaviour, one rule enforced on one surface only) are invisible to a fake.
///
/// The status lifecycle has none of that in it. It is a pure function of two enums —
/// no DbContext, no SQL, no surface — so a database here would buy nothing and cost a
/// container. What the database *can* still get wrong (does the refusal reach REST and
/// GraphQL identically, and does a refused PATCH leave the row alone) is covered where
/// it belongs, in Parity/SurfaceParityTests.
///
/// The table below is the business decision, written out in full rather than sampled.
/// All 25 combinations are here on purpose: this is the specification, and an omitted
/// row is a rule nobody agreed to.
/// </summary>
public class ApplicationStatusTransitionTests
{
    [Theory]
    // From Applied — everything is reachable, including a direct jump to Offer for the
    // referral / contract case where no interview stage was ever logged.
    [InlineData(ApplicationStatus.Applied, ApplicationStatus.Applied, true)]
    [InlineData(ApplicationStatus.Applied, ApplicationStatus.Interviewing, true)]
    [InlineData(ApplicationStatus.Applied, ApplicationStatus.Offer, true)]
    [InlineData(ApplicationStatus.Applied, ApplicationStatus.Rejected, true)]
    [InlineData(ApplicationStatus.Applied, ApplicationStatus.Withdrawn, true)]
    // From Interviewing — forwards or out, but not back to Applied.
    [InlineData(ApplicationStatus.Interviewing, ApplicationStatus.Applied, false)]
    [InlineData(ApplicationStatus.Interviewing, ApplicationStatus.Interviewing, true)]
    [InlineData(ApplicationStatus.Interviewing, ApplicationStatus.Offer, true)]
    [InlineData(ApplicationStatus.Interviewing, ApplicationStatus.Rejected, true)]
    [InlineData(ApplicationStatus.Interviewing, ApplicationStatus.Withdrawn, true)]
    // From Offer — only an outcome. An offer is not un-made by re-applying.
    [InlineData(ApplicationStatus.Offer, ApplicationStatus.Applied, false)]
    [InlineData(ApplicationStatus.Offer, ApplicationStatus.Interviewing, false)]
    [InlineData(ApplicationStatus.Offer, ApplicationStatus.Offer, true)]
    [InlineData(ApplicationStatus.Offer, ApplicationStatus.Rejected, true)]
    [InlineData(ApplicationStatus.Offer, ApplicationStatus.Withdrawn, true)]
    // From Rejected — closed, not terminal. Re-openable, re-labellable, but no offer.
    [InlineData(ApplicationStatus.Rejected, ApplicationStatus.Applied, true)]
    [InlineData(ApplicationStatus.Rejected, ApplicationStatus.Interviewing, true)]
    [InlineData(ApplicationStatus.Rejected, ApplicationStatus.Offer, false)]
    [InlineData(ApplicationStatus.Rejected, ApplicationStatus.Rejected, true)]
    [InlineData(ApplicationStatus.Rejected, ApplicationStatus.Withdrawn, true)]
    // From Withdrawn — same shape as Rejected.
    [InlineData(ApplicationStatus.Withdrawn, ApplicationStatus.Applied, true)]
    [InlineData(ApplicationStatus.Withdrawn, ApplicationStatus.Interviewing, true)]
    [InlineData(ApplicationStatus.Withdrawn, ApplicationStatus.Offer, false)]
    [InlineData(ApplicationStatus.Withdrawn, ApplicationStatus.Rejected, true)]
    [InlineData(ApplicationStatus.Withdrawn, ApplicationStatus.Withdrawn, true)]
    public void TheLifecycleTable(ApplicationStatus from, ApplicationStatus to, bool allowed)
        => Assert.Equal(allowed, ApplicationStatusTransitions.IsAllowed(from, to));

    [Fact]
    public void StayingPutIsAlwaysAllowed_IncludingFromAClosedStatus()
    {
        // A PATCH that re-sends the status it already has is not a transition, and a
        // PATCH that only touches Notes must not be refused because the application
        // happens to be Rejected. Worth its own test because it is the case that would
        // make the feature feel broken in ordinary use.
        foreach (var status in Enum.GetValues<ApplicationStatus>())
            Assert.True(ApplicationStatusTransitions.IsAllowed(status, status));
    }

    [Fact]
    public void EveryStatusHasARow_SoANewOneCannotCrashTheUpdatePath()
    {
        // IsAllowed indexes the table by the *current* status. An enum member with no
        // row would not fail to compile — it would throw KeyNotFoundException on a PATCH
        // of an application that happens to be in that status. This is the test that
        // turns adding a status without deciding its transitions into a red build
        // instead of a production 500.
        foreach (var from in Enum.GetValues<ApplicationStatus>())
        foreach (var to in Enum.GetValues<ApplicationStatus>())
            _ = ApplicationStatusTransitions.IsAllowed(from, to);
    }
}
