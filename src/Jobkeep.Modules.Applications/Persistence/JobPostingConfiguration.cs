using Jobkeep.Modules.Applications.Domain;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Jobkeep.SharedKernel;

namespace Jobkeep.Modules.Applications.Persistence;

// PHASE 13.3b: moved here from Jobkeep.Infrastructure.Data alongside the
// entity it maps, and ToTable gained the `applications` schema. Everything else is
// unchanged from 13.3a, which lifted it out of AppDbContext.OnModelCreating
// verbatim.
public class JobPostingConfiguration : IEntityTypeConfiguration<JobPosting>
{
    public void Configure(EntityTypeBuilder<JobPosting> e)
    {
        e.ToTable("job_postings", "applications");
        // PHASE 8 — soft delete. Same global filter as job_applications, and the
        // pairing matters: EF warns when a filtered principal is on the required
        // end of a relationship with an UNFILTERED dependent, because the join
        // then silently drops rows. Both ends carry the filter, so an archived ad
        // and its live application cannot coexist — DeletePostingHandler still
        // refuses while any live application names it.
        e.HasQueryFilter(QueryFilters.SoftDelete, p => !p.IsDeleted);

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
        //
        // PHASE 13.3b — the schema has to be repeated in this second ToTable
        // call. The lambda-only overload configures "the table this entity maps
        // to", and with a schema in play the safe form is the one that names both
        // again; the alternative reads as if the constraints attach to a
        // different, default-schema table of the same name.
        e.ToTable("job_postings", "applications", t =>
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
