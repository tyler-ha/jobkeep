using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// The import review cycle plus the four tables a confirmed draft becomes, in the
// `documents` schema since 13.3b. See ApplicationsDbContext for why the six 13.2
// interfaces became six real contexts.
//
// resume_skills is here and skills is not, and the split is the same one
// posting_skills makes on the Applications side: the LINK belongs to the module
// that owns the document, the shared vocabulary row does not.
public class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options)
    : DbContext(options)
{
    public DbSet<DocumentImport> DocumentImports => Set<DocumentImport>();
    public DbSet<Resume> Resumes => Set<Resume>();
    public DbSet<ResumeSkill> ResumeSkills => Set<ResumeSkill>();
    public DbSet<ResumeExperience> ResumeExperiences => Set<ResumeExperience>();
    public DbSet<ResumeEducation> ResumeEducations => Set<ResumeEducation>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyConfigurationsFromAssembly(typeof(DocumentsDbContext).Assembly);

        // LAST, deliberately — it reads the finished model.
        ModelConventions.ApplyDatabaseDefaults(model);
    }
}
