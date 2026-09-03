using Jobkeep.Models;
using Jobkeep.Modules.Ai;
using Jobkeep.Modules.Analytics;
using Jobkeep.Modules.Applications;
using Jobkeep.Modules.Ats;
using Jobkeep.Modules.Documents;
using Jobkeep.Modules.Skills;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Tests.Infrastructure;

/// <summary>
/// The god-context, and it lives in tests/ on purpose.
///
/// <para>
/// PHASE 13.3b split <c>AppDbContext</c> into six per-module contexts. That is the
/// deliverable and it is not negotiable in <c>src/</c>. But <b>122 call sites across
/// 15 test files</b> reach a context directly to ARRANGE rows, several of them mixing
/// modules in a single block — <c>AtsTests.SeedResumeAsync</c> reads <c>db.Skills</c>
/// while writing <c>db.Resumes</c>. Rewriting all 122 into six contexts would be a
/// large, mechanical, entirely un-reviewable diff attached to a change whose whole
/// value is that behaviour did not move.
/// </para>
///
/// <para>
/// So the tests keep one context that can see everything, and it is declared here.
/// The property that matters is preserved and arguably sharpened: <b>no assembly in
/// <c>src/</c> can name this type</b>, because <c>tests/</c> references <c>src/</c>
/// and not the other way round. A module cannot cheat with it even by accident, and
/// <c>ModuleBoundaryTests</c> asserts that no module constructor takes a context
/// declared outside its own assembly, which catches this one along with the old
/// <c>AppDbContext</c> shape it replaces.
/// </para>
///
/// <para>
/// A test that wants to prove something about a MODULE's boundary should resolve
/// that module's own context rather than this one. This is for arranging rows and
/// for asserting on what Postgres actually holds.
/// </para>
///
/// <para>
/// <b>Why this is correct rather than merely convenient:</b> every configuration
/// names its schema in <c>ToTable</c>'s second argument rather than through
/// <c>HasDefaultSchema</c> on a context. So applying all six assemblies' configurations
/// to one model produces exactly the same mapping the six real contexts produce, table
/// for table and schema for schema. Had the schema been a property of the context, this
/// class would have had to pick one and would have been wrong about thirteen tables.
/// </para>
/// </summary>
public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    // The one place the six module assemblies are named together. Also what the
    // configuration scan runs over, so a new module is added here and nowhere else.
    private static readonly System.Reflection.Assembly[] ModuleAssemblies =
    [
        typeof(ApplicationsDbContext).Assembly,
        typeof(SkillsDbContext).Assembly,
        typeof(DocumentsDbContext).Assembly,
        typeof(AiDbContext).Assembly,
        typeof(AtsDbContext).Assembly,
        typeof(AnalyticsDbContext).Assembly,
    ];

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<JobPosting> JobPostings => Set<JobPosting>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<PostingSkill> PostingSkills => Set<PostingSkill>();
    public DbSet<JobRequirement> JobRequirements => Set<JobRequirement>();
    public DbSet<JobApplication> JobApplications => Set<JobApplication>();
    public DbSet<AiAnalysis> AiAnalyses => Set<AiAnalysis>();
    public DbSet<AtsResult> AtsResults => Set<AtsResult>();

    public DbSet<DocumentImport> DocumentImports => Set<DocumentImport>();
    public DbSet<Resume> Resumes => Set<Resume>();
    public DbSet<ResumeSkill> ResumeSkills => Set<ResumeSkill>();
    public DbSet<ResumeExperience> ResumeExperiences => Set<ResumeExperience>();
    public DbSet<ResumeEducation> ResumeEducations => Set<ResumeEducation>();

    // The three views Applications publishes. Read-only and keyless, exactly as
    // AnalyticsDbContext maps them.
    public DbSet<ApplicationStatusCount> ApplicationStatusCounts => Set<ApplicationStatusCount>();
    public DbSet<CompanyApplicationCount> CompanyApplicationCounts => Set<CompanyApplicationCount>();
    public DbSet<PostingSkillDemand> PostingSkillDemands => Set<PostingSkillDemand>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        foreach (var assembly in ModuleAssemblies)
            model.ApplyConfigurationsFromAssembly(assembly);

        // LAST, for the same reason each real context calls it last: it reads the
        // finished model. If this context and the real ones disagreed about that,
        // a test could pass against defaults the app does not have.
        ModelConventions.ApplyDatabaseDefaults(model);
    }
}
