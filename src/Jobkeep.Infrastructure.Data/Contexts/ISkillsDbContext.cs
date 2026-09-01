using Jobkeep.Models;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Data;

// The shared skill taxonomy. See IApplicationsDbContext for why these six
// interfaces live in this project rather than in Contracts or in the modules.
//
// Only Jobkeep.Modules.Skills holds this. Every other module reaches skills
// through ISkillCatalog, because a skill row is co-owned in practice —
// posting_skills (Applications) and resume_skills (Documents) point at the same
// row, and that shared row is what the Phase 7 natural key and the Phase 5 skill
// gap both turn on. Four modules find-or-creating against one table by hand is
// four places to get NaturalKey.Of wrong.
public interface ISkillsDbContext
{
    DbSet<Skill> Skills { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
