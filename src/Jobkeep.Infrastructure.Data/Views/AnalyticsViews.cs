using Jobkeep.Models;

namespace Jobkeep.Data;

// ---------------------------------------------------------------------------
// PHASE 13.2 — the three read models Applications PUBLISHES to Analytics
// ---------------------------------------------------------------------------
// Analytics owns no tables and asks questions about other modules'. Until now it
// answered them by querying those tables directly, which architecture.md
// decision 13 allowed on the grounds that a read-only module "can never leave
// another module's data in a state that module did not choose". That argument is
// about *safety*, and it is still true. It was never an argument about
// *extractability*, and extractability is what this phase is buying: a module
// that SELECTs from another module's tables cannot be lifted into its own
// deployable without those tables coming with it.
//
// The alternative that was refused, twice, is a contract with one method per
// question — IJobApplicationRepository under a new name, and unbounded by
// construction because there is no limit on how many questions a reporting
// module has (decision 5, then decision 13's own reasoning).
//
// A published view is the third option and it is what a real service split uses:
// the owning module decides the shape it is willing to expose, the aggregate
// still runs in Postgres, and the coupling is to a *published interface* rather
// than to a table layout. At 13.3 each view moves into its owner's schema; at
// extraction it becomes a read replica, a materialised view or a feed, and the
// consuming code does not change either time.
//
// These are keyless types (HasNoKey + ToView in AppDbContext). EF never writes
// them, and it does not scaffold the views themselves — the SQL is hand-written
// in the AnalyticsViews migration with a matching Down.

// SELECT "Status", COUNT(*) FROM job_applications GROUP BY "Status".
//
// Empty stages have no row to group and so are absent here, exactly as they were
// when this was a GROUP BY in the slice. StatusFunnel still zero-fills from the
// enum, which is O(stages) rather than O(rows).
public class ApplicationStatusCount
{
    public ApplicationStatus Status { get; set; }
    public int Count { get; set; }
}

// Applications per company, joined through the posting. Names the company rather
// than its id because that is the whole response — and because a rollup keyed on
// an id the caller cannot resolve would need a second published view to be
// useful.
public class CompanyApplicationCount
{
    public string CompanyName { get; set; } = "";
    public int ApplicationCount { get; set; }
}

// How many distinct postings ask for each skill.
//
// Stops at SkillId ON PURPOSE. Joining `skills` here would put another module's
// table inside Applications' published view — the exact coupling the view exists
// to remove, moved from C# into SQL where no compiler would catch it. Analytics
// resolves the ids to names through ISkillCatalog; see SkillDemand.cs for what
// that costs.
//
// The count is of posting_skills rows, and the composite PK makes that a count
// of postings: one posting contributes one row even if you applied to it twice.
public class PostingSkillDemand
{
    public Guid SkillId { get; set; }
    public int PostingCount { get; set; }
}
