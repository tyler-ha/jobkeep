using Jobkeep.Models;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Data;

// ---------------------------------------------------------------------------
// PHASE 13.2 — the seam, and why the six interfaces live in THIS project
// ---------------------------------------------------------------------------
// Each module gets an interface exposing only the DbSets it owns, and
// AppDbContext implements all six. Nothing moves in Postgres: this is the
// logical decoupling, done first and on its own, so 13.3 can split the schema
// without also being the step that changes behaviour. That separation is the
// lesson 13.1 already paid for — it tried to split the entity classes and the
// foreign keys in one step and had to quarantine them here instead.
//
// Three places these could have gone, and only one of them works:
//
//   * Jobkeep.Contracts. No. Contracts becomes the wire schema when a module is
//     extracted, so everything in it has to survive a network hop, and a
//     DbSet<T> cannot. ModuleBoundaryTests.Foundation_projects_depend_on_nothing
//     _of_ours enforces the emptier half of that rule already.
//   * Each module's own project. No, and this one is not a matter of taste:
//     AppDbContext implements them, so Jobkeep.Infrastructure.Data would have to
//     reference all six module projects while they reference it. That is a
//     circular project reference and it does not compile.
//   * Here. The modules already reference this project, and this project is
//     scheduled for deletion in 13.3 — at which point each interface is replaced
//     by a real per-module DbContext and these files go with it.
//
// So the interfaces are deliberately temporary scaffolding. What they buy in the
// meantime is the thing the phase is about: a module physically cannot name
// another module's table, because the type it holds does not have the property.
// The compiler, not a convention in a doc, is what says so.
//
// SaveChangesAsync is on every one of them. A unit of work that cannot be
// committed is not a unit of work, and each module still commits its own writes.

// The tables Applications owns: an application, the posting it points at, the
// company that posted it, and the posting's skill links and requirements.
//
// `Skills` is NOT here. The skill rows are shared vocabulary — posting_skills
// and resume_skills both point at them — so they are reached through
// ISkillCatalog by every module including this one. 13.3 gives them their own
// schema; Jobkeep.Modules.Skills already exists to own the code.
public interface IApplicationsDbContext
{
    DbSet<Company> Companies { get; }
    DbSet<JobPosting> JobPostings { get; }
    DbSet<PostingSkill> PostingSkills { get; }
    DbSet<JobRequirement> JobRequirements { get; }
    DbSet<JobApplication> JobApplications { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
