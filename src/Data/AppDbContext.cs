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

    // Phase 4.5 — document import. `document_imports` holds the review-cycle
    // drafts; the four resume tables hold what a confirmed draft became.
    public DbSet<DocumentImport> DocumentImports => Set<DocumentImport>();
    public DbSet<Resume> Resumes => Set<Resume>();
    public DbSet<ResumeSkill> ResumeSkills => Set<ResumeSkill>();
    public DbSet<ResumeExperience> ResumeExperiences => Set<ResumeExperience>();
    public DbSet<ResumeEducation> ResumeEducations => Set<ResumeEducation>();

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

            // Phase 4.5 — which resume version was sent. Restrict, not Cascade:
            // deleting a resume you have applied with must not silently delete
            // the applications that used it. The user has to break the link
            // deliberately, which is the whole reason the resume stopped being a
            // column on this table.
            e.HasOne(a => a.Resume)
                .WithMany(r => r.Applications)
                .HasForeignKey(a => a.ResumeId)
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

        // -------------------------------------------------------------------
        // Phase 4.5 — document import
        // -------------------------------------------------------------------
        model.Entity<DocumentImport>(e =>
        {
            e.ToTable("document_imports");
            e.Property(d => d.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.Format).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.FileName).HasMaxLength(260);
            e.Property(d => d.ContentHash).HasMaxLength(64);   // SHA-256 as hex

            // jsonb, not text: Postgres validates the structure on write and the
            // column is inspectable with -> in psql when a draft looks wrong.
            // See DocumentImport.DraftJson for why the draft is a document rather
            // than five mirror tables.
            e.Property(d => d.DraftJson).HasColumnType("jsonb");

            // The review queue is the only way this table is ever read in bulk
            // ("what am I still to confirm"), so it gets the index and nothing
            // else does. Note this is a filtered index on an enum stored as
            // text — the comparison is against the string name, not the int.
            e.HasIndex(d => d.Status);
        });

        model.Entity<Resume>(e =>
        {
            e.ToTable("resumes");
            e.Property(r => r.Label).HasMaxLength(100);
            e.Property(r => r.FullName).HasMaxLength(200);
            e.Property(r => r.Email).HasMaxLength(320);      // RFC 5321 maximum
            e.Property(r => r.Phone).HasMaxLength(50);
            e.Property(r => r.Location).HasMaxLength(200);
            e.Property(r => r.SourceFileName).HasMaxLength(260);
            e.Property(r => r.SourceHash).HasMaxLength(64);

            // Unique label, so importing twice under one name is a conflict the
            // user resolves rather than two rows called the same thing. Same
            // reasoning as companies.Name, and the same known limitation: the
            // uniqueness is case-sensitive, so "Backend" and "backend" are two
            // resumes. That is the dedup gap already recorded against skills and
            // companies (CLAUDE.md), and it is left consistent here on purpose —
            // fixing one table would make the three disagree. Phase 2.7 fixes
            // all of them together with a case-insensitive natural key.
            e.HasIndex(r => r.Label).IsUnique();
        });

        model.Entity<ResumeSkill>(e =>
        {
            e.ToTable("resume_skills");
            // Composite key: a skill appears at most once per resume. The exact
            // mirror of posting_skills, so a retried write is a no-op rather than
            // a duplicate row.
            e.HasKey(rs => new { rs.ResumeId, rs.SkillId });
            e.Property(rs => rs.Source).HasConversion<string>().HasMaxLength(20);

            e.HasOne(rs => rs.Resume)
                .WithMany(r => r.ResumeSkills)
                .HasForeignKey(rs => rs.ResumeId)
                .OnDelete(DeleteBehavior.Cascade);    // deleting a resume drops its links

            e.HasOne(rs => rs.Skill)
                .WithMany(s => s.ResumeSkills)
                .HasForeignKey(rs => rs.SkillId)
                .OnDelete(DeleteBehavior.Restrict);   // but never the shared skill row
        });

        model.Entity<ResumeExperience>(e =>
        {
            e.ToTable("resume_experiences");
            e.Property(x => x.Employer).HasMaxLength(200);
            e.Property(x => x.Title).HasMaxLength(200);
            // Free text, not dates — see the comment on the model for why
            // "Mar 2021" is stored as "Mar 2021".
            e.Property(x => x.StartText).HasMaxLength(50);
            e.Property(x => x.EndText).HasMaxLength(50);
            // List<string> -> Postgres text[], the same mapping AtsResult uses.
            e.Property(x => x.Highlights).HasColumnType("text[]");

            e.HasOne(x => x.Resume)
                .WithMany(r => r.Experiences)
                .HasForeignKey(x => x.ResumeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ResumeEducation>(e =>
        {
            e.ToTable("resume_educations");
            e.Property(x => x.Institution).HasMaxLength(200);
            e.Property(x => x.Qualification).HasMaxLength(200);
            e.Property(x => x.YearText).HasMaxLength(50);

            e.HasOne(x => x.Resume)
                .WithMany(r => r.Educations)
                .HasForeignKey(x => x.ResumeId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
