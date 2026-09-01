using Jobkeep.Models;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Data;

// The import review cycle plus the four tables a confirmed draft becomes.
// See IApplicationsDbContext for why these six interfaces live in this project.
//
// resume_skills is here and skills is not, and the split is the same one
// posting_skills makes on the Applications side: the LINK belongs to the module
// that owns the document, the shared vocabulary row does not.
public interface IDocumentsDbContext
{
    DbSet<DocumentImport> DocumentImports { get; }
    DbSet<Resume> Resumes { get; }
    DbSet<ResumeSkill> ResumeSkills { get; }
    DbSet<ResumeExperience> ResumeExperiences { get; }
    DbSet<ResumeEducation> ResumeEducations { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
