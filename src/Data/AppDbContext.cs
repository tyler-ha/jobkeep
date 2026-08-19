using Jobkeep.Models;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Data;

// The EF Core unit-of-work. All relational mapping lives here in OnModelCreating
// (Fluent API) rather than scattered as attributes on the model classes, so the
// schema decisions are readable in one place.
public class AppDbContext : DbContext
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

    protected override void OnModelCreating(ModelBuilder model)
    {
        // Store every enum as its string name (e.g. "Applied") instead of an int,
        // so raw rows are self-explanatory. Applied per-property below.
        model.Entity<Company>(e =>
        {
            e.ToTable("companies");
            // Unique name backs the find-or-create-by-name dedup in the repository.
            e.HasIndex(c => c.Name).IsUnique();
            e.Property(c => c.Name).HasMaxLength(200);
        });

        model.Entity<JobPosting>(e =>
        {
            e.ToTable("job_postings");
            e.Property(p => p.Title).HasMaxLength(300);
            e.Property(p => p.EmploymentType).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.SalaryPeriod).HasConversion<string>().HasMaxLength(10);
            e.Property(p => p.SalaryCurrency).HasMaxLength(3);
            e.Property(p => p.SalaryMin).HasPrecision(12, 2);
            e.Property(p => p.SalaryMax).HasPrecision(12, 2);

            // A posting belongs to one company; block deleting a company that
            // still has postings (Restrict) rather than silently cascading.
            e.HasOne(p => p.Company)
                .WithMany(c => c.Postings)
                .HasForeignKey(p => p.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Skill>(e =>
        {
            e.ToTable("skills");
            e.HasIndex(s => s.Name).IsUnique();
            e.Property(s => s.Name).HasMaxLength(100);
            e.Property(s => s.Category).HasMaxLength(50);
        });

        model.Entity<PostingSkill>(e =>
        {
            e.ToTable("posting_skills");
            // Composite key: a skill appears at most once per posting.
            e.HasKey(ps => new { ps.PostingId, ps.SkillId });
            e.Property(ps => ps.Source).HasConversion<string>().HasMaxLength(20);

            e.HasOne(ps => ps.Posting)
                .WithMany(p => p.PostingSkills)
                .HasForeignKey(ps => ps.PostingId)
                .OnDelete(DeleteBehavior.Cascade);   // deleting a posting drops its skill links

            e.HasOne(ps => ps.Skill)
                .WithMany(s => s.PostingSkills)
                .HasForeignKey(ps => ps.SkillId)
                .OnDelete(DeleteBehavior.Restrict);  // but never delete the shared skill row
        });

        model.Entity<JobRequirement>(e =>
        {
            e.ToTable("job_requirements");
            e.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasOne(r => r.Posting)
                .WithMany(p => p.Requirements)
                .HasForeignKey(r => r.PostingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<AiAnalysis>(e =>
        {
            e.ToTable("ai_analyses");
            e.Property(a => a.Seniority).HasConversion<string>().HasMaxLength(20);
            // 1:1 — one analysis per posting.
            e.HasOne(a => a.Posting)
                .WithOne(p => p.AiAnalysis)
                .HasForeignKey<AiAnalysis>(a => a.PostingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<JobApplication>(e =>
        {
            e.ToTable("job_applications");
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);

            // The application points at a posting, but deleting the application
            // must NOT delete the posting (a posting can have several applications).
            e.HasOne(a => a.Posting)
                .WithMany(p => p.Applications)
                .HasForeignKey(a => a.PostingId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<AtsResult>(e =>
        {
            e.ToTable("ats_results");
            // List<string> -> Postgres text[] (Npgsql handles the array mapping).
            e.Property(r => r.MatchedKeywords).HasColumnType("text[]");
            e.Property(r => r.MissingMustHaveKeywords).HasColumnType("text[]");
            e.Property(r => r.FormattingRiskNotes).HasColumnType("text[]");

            // 1:1 — one ATS result per application; cascade on application delete.
            e.HasOne(r => r.Application)
                .WithOne(a => a.AtsResult)
                .HasForeignKey<AtsResult>(r => r.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
