using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Skills;

// The shared skill taxonomy, and since 13.3b its own schema and its own
// migration history. See ApplicationsDbContext for why the six 13.2 interfaces
// were replaced by six real contexts rather than kept in front of them.
//
// Only SkillCatalog holds this. Every other module reaches skills through
// ISkillCatalog, because a skill row is co-owned in practice: posting_skills
// (Applications) and resume_skills (Documents) point at the same row, and that
// shared row is what the Phase 7 natural key and the Phase 5 skill gap both turn
// on. Four modules find-or-creating against one table by hand is four places to
// get NaturalKey.Of wrong.
//
// That co-ownership is also why this context's separateness bites hardest.
// FindOrCreateAsync saves through THIS context, which since 13.3b is a different
// unit of work from the caller's — so the save is genuinely a separate
// transaction now, rather than the same one it used to share. The ordering rule
// its comment states (call the catalog before adding rows of your own) was
// belt-and-braces under one shared AppDbContext; it is load-bearing here.
public class SkillsDbContext(DbContextOptions<SkillsDbContext> options)
    : DbContext(options)
{
    public DbSet<Skill> Skills => Set<Skill>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyConfigurationsFromAssembly(typeof(SkillsDbContext).Assembly);

        // LAST, deliberately — it reads the finished model.
        ModelConventions.ApplyDatabaseDefaults(model);
    }
}
