using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Data.Configurations;

// PHASE 13.3a: lifted out of AppDbContext.OnModelCreating unchanged. The class
// moves to Jobkeep.Modules.Applications in 13.3b, where ToTable also gains its schema.
public class JobPostingConfiguration : IEntityTypeConfiguration<JobPosting>
{
    public void Configure(EntityTypeBuilder<JobPosting> e)
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

        ModelConventions.UseXmin(e);

        // A posting belongs to one company; block deleting a company that
        // still has postings (Restrict) rather than silently cascading.
        e.HasOne(p => p.Company)
            .WithMany(c => c.Postings)
            .HasForeignKey(p => p.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
