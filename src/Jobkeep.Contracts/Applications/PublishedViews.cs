using Jobkeep.Contracts.Shared;
using Jobkeep.Contracts.Skills;

namespace Jobkeep.Contracts.Applications;

// ---------------------------------------------------------------------------
// The three read models Applications PUBLISHES to Analytics
// ---------------------------------------------------------------------------
// Analytics owns no tables and asks questions about other modules'. Until 13.2 it
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
// than to a table layout. The views now live in the `applications` schema; at
// extraction each becomes a read replica, a materialised view or a feed, and the
// consuming code does not change either time.
//
// ---------------------------------------------------------------------------
// PHASE 13.3b — why the SHAPE is here and the MAPPING is not
// ---------------------------------------------------------------------------
// Until 13.3b these three types sat beside AppDbContext with their EF
// configurations, and those configurations carried a comment saying the mapping
// "belongs to APPLICATIONS, because Applications publishes it". That sentence
// could not survive the split, and it is worth recording why rather than quietly
// deleting it: AnalyticsDbContext is the context that READS the views, it can
// only apply its own assembly's configurations, and Analytics may not reference
// Applications. Leaving the mapping on the publishing side would have meant
// Analytics could not read its own three questions.
//
// The split that works is publisher-owns-the-definition, consumer-owns-the-read:
//
//   * The PAYLOAD SHAPE is here, in Contracts. These types are literally
//     Applications' published interface, and Contracts is defined as the thing
//     that crosses a boundary and becomes the wire schema on extraction.
//   * The SQL is in Applications' initial migration. The publisher decides what
//     the view means; nobody else can change it.
//   * The EF MAPPING (HasNoKey + ToView("v_...", "applications")) is in
//     Analytics, because reading is Analytics' problem. Analytics naming the
//     `applications` schema is correct and not a leak — that schema is the
//     view's published ADDRESS, and at extraction it becomes a URL.
//
// These are keyless types. EF never writes them, and it does not scaffold the
// views themselves — the SQL is hand-written in Applications' initial migration
// with a matching Down.

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
// to remove, moved from C# into SQL where no compiler would catch it. Since
// 13.3b that join could not even be written: `skills` is a different schema with
// its own migration history. Analytics resolves the ids to names through
// ISkillCatalog; see SkillDemand.cs for what that costs.
//
// The count is of posting_skills rows, and the composite PK makes that a count
// of postings: one posting contributes one row even if you applied to it twice.
public class PostingSkillDemand
{
    public Guid SkillId { get; set; }
    public int PostingCount { get; set; }
}
