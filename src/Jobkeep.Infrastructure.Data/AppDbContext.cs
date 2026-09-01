using Jobkeep.Models;
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

    protected override void OnModelCreating(ModelBuilder model)
    {
        // Phase 7, F7 — optimistic concurrency via Postgres's own `xmin` system
        // column, which every row already has. Zero added columns and no schema
        // change on the table itself: EF maps it as a shadow property, reads it
        // with the row, and adds it to the UPDATE's WHERE clause. A second write
        // against a stale copy then matches no rows and EF raises
        // DbUpdateConcurrencyException instead of silently discarding the first.
        //
        // Written out rather than using the provider's old
        // `UseXminAsConcurrencyToken()` helper, which was REMOVED in the Npgsql
        // 7 provider — this is the shape its own migration guidance replaced it
        // with. Applied to the three tables with a read-modify-write update
        // path; link and child rows are insert-or-delete and have no lost-update
        // to lose.
        static void UseXmin<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e)
            where T : class
            => e.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

        // Store every enum as its string name (e.g. "Applied") instead of an int,
        // so raw rows are self-explanatory. Applied per-property below.
        model.Entity<Company>(e =>
        {
            e.ToTable("companies");
            e.Property(c => c.Name).HasMaxLength(200);

            // Phase 7 — the case-insensitive natural key. The unique index moved
            // OFF Name and onto a STORED generated column, so "Canva" and "canva"
            // collide instead of becoming two employers with one rollup each.
            //
            // A generated column rather than an expression index (CREATE UNIQUE
            // INDEX ... ON companies (lower(name))) because EF cannot model the
            // latter: it would have to be hand-written into the migration and the
            // model snapshot would then disagree with the database forever. A
            // generated column IS in the model, so `dotnet ef migrations add`
            // keeps producing correct migrations after this one.
            e.Property(c => c.NameNormalized)
                .HasMaxLength(200)
                .HasComputedColumnSql("lower(\"Name\")", stored: true);
            e.HasIndex(c => c.NameNormalized).IsUnique();

            // F13 — the three columns that were unbounded `text`.
            e.Property(c => c.Website).HasMaxLength(500);
            e.Property(c => c.Industry).HasMaxLength(100);
            e.Property(c => c.HqLocation).HasMaxLength(200);

            // F7 — xmin is Postgres's own row version, so this costs zero added
            // columns. A concurrent overwrite now throws
            // DbUpdateConcurrencyException instead of silently discarding the
            // other write.
            UseXmin(e);
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

            // F13 — previously unbounded `text`.
            e.Property(p => p.Location).HasMaxLength(200);
            e.Property(p => p.SourceUrl).HasMaxLength(2000);
            // Description holds a whole pasted job ad, so the bound is generous
            // rather than tight. The point is that it HAS one: on an
            // unauthenticated write endpoint an unbounded text column is a
            // storage vector, and 20k characters is far past any real ad.
            e.Property(p => p.Description).HasMaxLength(20000);

            // F12 — the two CHECK constraints the schema was missing. Table-level
            // because both are statements about a row rather than a column, and
            // in the database rather than in C# because a rule enforced only in
            // the app is one any other writer (a psql fix, a backfill, a future
            // service) can ignore without noticing.
            e.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "ck_job_postings_salary_range",
                    "\"SalaryMin\" IS NULL OR \"SalaryMax\" IS NULL OR \"SalaryMin\" <= \"SalaryMax\"");

                // ISO-4217 is three uppercase letters. varchar(3) accepted "XX!"
                // before this; it does not now.
                t.HasCheckConstraint(
                    "ck_job_postings_currency_iso4217",
                    "\"SalaryCurrency\" ~ '^[A-Z]{3}$'");
            });

            UseXmin(e);

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
            e.Property(s => s.Name).HasMaxLength(100);
            e.Property(s => s.Category).HasMaxLength(50);

            // Phase 7 — see companies above. This is the table where the defect
            // was costing something measurable: a duplicate row split one
            // skill's count in /stats/skill-demand, and the Phase 5 ATS check
            // matches skill ROWS, so a difference of case read as a gap.
            e.Property(s => s.NameNormalized)
                .HasMaxLength(100)
                .HasComputedColumnSql("lower(\"Name\")", stored: true);
            e.HasIndex(s => s.NameNormalized).IsUnique();
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
            e.Property(r => r.Text).HasMaxLength(1000);   // F13
            e.HasOne(r => r.Posting)
                .WithMany(p => p.Requirements)
                .HasForeignKey(r => r.PostingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<AiAnalysis>(e =>
        {
            e.ToTable("ai_analyses");
            e.Property(a => a.Seniority).HasConversion<string>().HasMaxLength(20);
            // F13. ModelUsed holds an identifier like "llama3.2:3b" — the
            // clearest case in the audit that a bound was simply forgotten.
            e.Property(a => a.Summary).HasMaxLength(4000);
            e.Property(a => a.ModelUsed).HasMaxLength(100);
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
            e.Property(a => a.Notes).HasMaxLength(10000);   // F13

            // F14 — the indexes behind the default query. ListApplications sorts
            // on DateApplied descending and filters on Status; before this only
            // the foreign keys were indexed.
            //
            // Phase 2.3 shipped the filtering and deliberately parked these so
            // this phase stays one migration, reasoning that an index added
            // before the query pattern settles is a guess. It has settled. The
            // descending order is not decoration: it matches the sort, so
            // Postgres can walk the index instead of sorting the result.
            e.HasIndex(a => a.Status);
            e.HasIndex(a => a.DateApplied).IsDescending();

            UseXmin(e);

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
            e.Property(r => r.MissingNiceToHaveKeywords).HasColumnType("text[]");
            e.Property(r => r.UnmetRequirements).HasColumnType("text[]");
            e.Property(r => r.FormattingRiskNotes).HasColumnType("text[]");
            e.Property(r => r.Warning).HasMaxLength(500);   // F13

            // 1:1 — one ATS result per application; cascade on application delete.
            e.HasOne(r => r.Application)
                .WithOne(a => a.AtsResult)
                .HasForeignKey<AtsResult>(r => r.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Phase 5 — which resume was judged. Restrict, matching
            // job_applications.ResumeId: deleting a resume must not silently
            // delete the evidence of what it was checked against. The result
            // stays 1:1 with the application (re-checking overwrites, latest
            // wins, the shape ai_analyses already uses), so this column is what
            // tells you which resume the surviving row read.
            e.HasOne(r => r.Resume)
                .WithMany()
                .HasForeignKey(r => r.ResumeId)
                .OnDelete(DeleteBehavior.Restrict);
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
            // Same string-enum treatment as document_imports.Format, so the two
            // columns recording the same fact read the same way in psql.
            e.Property(r => r.SourceFormat).HasConversion<string>().HasMaxLength(20);

            // Unique label, so importing twice under one name is a conflict the
            // user resolves rather than two rows called the same thing. Same
            // reasoning as companies.Name, and the same known limitation: the
            // uniqueness is case-sensitive, so "Backend" and "backend" are two
            // resumes. That is the dedup gap already recorded against skills and
            // companies (CLAUDE.md), and it is left consistent here on purpose —
            // fixing one table would make the three disagree.
            //
            // PHASE 7 FIXED ALL THREE. The unique index moved off Label onto the
            // generated LabelNormalized column, so "Backend" and "backend" are
            // now one resume — matching companies.Name and skills.Name, which is
            // the consistency 4.5 was protecting when it left the defect in
            // rather than half-fixing it.
            e.Property(r => r.LabelNormalized)
                .HasMaxLength(100)
                .HasComputedColumnSql("lower(\"Label\")", stored: true);
            e.HasIndex(r => r.LabelNormalized).IsUnique();
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

        // -------------------------------------------------------------------
        // Phase 13.2 — the views Applications publishes to Analytics
        // -------------------------------------------------------------------
        // HasNoKey + ToView: EF reads them and will never try to write them, and
        // it leaves them out of the migration model entirely — the CREATE VIEW
        // statements are hand-written in the AnalyticsViews migration, because a
        // view is not something EF scaffolds.
        //
        // They are mapped here rather than in Analytics because Applications
        // publishes them. That is the whole point of the shape: the owner
        // decides what it exposes. At 13.3 they move into the `applications`
        // schema with the tables they read.
        model.Entity<ApplicationStatusCount>(e =>
        {
            e.HasNoKey().ToView("v_application_status_counts");
            // The underlying column is text (HasConversion<string> on
            // JobApplication.Status), so the view's column is text too and the
            // same conversion has to be declared on the way back in.
            e.Property(x => x.Status).HasConversion<string>();
        });

        model.Entity<CompanyApplicationCount>(e =>
            e.HasNoKey().ToView("v_company_application_counts"));

        model.Entity<PostingSkillDemand>(e =>
            e.HasNoKey().ToView("v_posting_skill_demand"));

        // -------------------------------------------------------------------
        // Phase 7, F11 — database-side defaults, applied as a convention
        // -------------------------------------------------------------------
        // Before this the schema had NO defaults at all: every id and every
        // timestamp originated in a C# property initialiser. That is fine while
        // this application is the only writer and wrong the moment anything else
        // is — a migration backfill, a `psql` fix, a future extracted service.
        // Such a writer could insert a row with a null timestamp or a zero GUID
        // and the schema would not notice, because the invariant lived in a
        // language the database does not speak.
        //
        // Applied in a loop rather than as twenty repeated lines for the same
        // reason F8 got an interceptor: a rule that must hold for every entity
        // should not depend on remembering it for every entity. A new table
        // picks this up by existing.
        //
        // Note these defaults are almost never exercised in normal operation —
        // EF always sends a value, so Postgres never falls back. That is the
        // point. They are the floor under everyone who is not EF.
        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetDeclaredProperties())
            {
                // gen_random_uuid() is built in from Postgres 13; no pgcrypto
                // extension needed, which matters because Neon's free tier is
                // the deploy target and an extension is one more thing to
                // provision.
                if (property.ClrType == typeof(Guid) && property.IsPrimaryKey())
                    property.SetDefaultValueSql("gen_random_uuid()");

                // The audit pair only. Domain timestamps that mean something
                // more specific — AnalyzedAtUtc, CheckedAtUtc, CommittedAtUtc —
                // are set when that thing happened, not when the row was
                // written, so a default would be actively misleading.
                if (property.ClrType == typeof(DateTime)
                    && property.Name is "CreatedAtUtc" or "UpdatedAtUtc")
                    property.SetDefaultValueSql("now() at time zone 'utc'");
            }
        }
    }
}
