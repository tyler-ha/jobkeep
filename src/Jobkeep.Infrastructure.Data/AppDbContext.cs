using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Data;

// The EF Core unit-of-work. All relational mapping lives here in OnModelCreating
// (Fluent API) rather than scattered as attributes on the model classes, so the
// schema decisions are readable in one place.
//
// PHASE 13.2 — it now implements six per-module interfaces, each exposing only
// that module's own DbSets, and no module names this class any more. The
// existing properties satisfy them as they stand, so nothing below changed:
// what changed is that a handler holding IDocumentsDbContext has no `Skills`
// property to reach for. Contexts/IApplicationsDbContext.cs has the reasoning,
// including why the interfaces live in this project. All of it — the interfaces
// and this class — is replaced by six real contexts in 13.3.
public class AppDbContext
    : DbContext,
      IApplicationsDbContext,
      ISkillsDbContext,
      IDocumentsDbContext,
      IAiDbContext,
      IAtsDbContext,
      IAnalyticsDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<JobPosting> JobPostings => Set<JobPosting>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<PostingSkill> PostingSkills => Set<PostingSkill>();
    public DbSet<JobRequirement> JobRequirements => Set<JobRequirement>();
    public DbSet<JobApplication> JobApplications => Set<JobApplication>();
    public DbSet<AiAnalysis> AiAnalyses => Set<AiAnalysis>();
    public DbSet<AtsResult> AtsResults => Set<AtsResult>();

    // Phase 4.5 — document import. `document_imports` holds the review-cycle
    // drafts; the four resume tables hold what a confirmed draft became.
    public DbSet<DocumentImport> DocumentImports => Set<DocumentImport>();
    public DbSet<Resume> Resumes => Set<Resume>();
    public DbSet<ResumeSkill> ResumeSkills => Set<ResumeSkill>();
    public DbSet<ResumeExperience> ResumeExperiences => Set<ResumeExperience>();
    public DbSet<ResumeEducation> ResumeEducations => Set<ResumeEducation>();

    // Phase 13.2 — the three views Applications publishes to Analytics. Keyless,
    // read-only, and mapped at the bottom of OnModelCreating. Views/AnalyticsViews.cs
    // has the argument for publishing a view instead of exposing the tables.
    public DbSet<ApplicationStatusCount> ApplicationStatusCounts => Set<ApplicationStatusCount>();
    public DbSet<CompanyApplicationCount> CompanyApplicationCounts => Set<CompanyApplicationCount>();
    public DbSet<PostingSkillDemand> PostingSkillDemands => Set<PostingSkillDemand>();

    // PHASE 13.3a — this was 400 lines of Fluent API. It is now 16
    // IEntityTypeConfiguration<T> classes in Configurations/, one per entity,
    // plus the two model-wide rules in Jobkeep.Persistence.
    //
    // The split is not tidying. Each configuration is a self-contained statement
    // about ONE table, so 13.3b moves it into the module that owns that table by
    // moving the file — where before, splitting this method would have meant
    // reading 400 lines and deciding which brace belonged to whom, in the same
    // step that changes the schema. That is the mistake 13.1 already paid for
    // once and its deviation note records.
    //
    // ApplyConfigurationsFromAssembly finds them by scanning THIS assembly for
    // the interface. In 13.3b each context scans its own module assembly and
    // therefore maps exactly its own tables, which is the property that makes a
    // context per module correct by construction rather than by review.
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // LAST, deliberately. It reads the finished model, so anything
        // configured after it would silently miss the defaults.
        ModelConventions.ApplyDatabaseDefaults(model);
    }
}
