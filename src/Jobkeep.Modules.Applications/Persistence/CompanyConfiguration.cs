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
public class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> e)
    {
        e.ToTable("companies", "applications");
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
        // PHASE 11.2b — the owner joins the key. "Google" was globally unique, so
        // the second user to apply there would have been refused by an index over
        // a row they cannot see: a 500 with no explanation available to them.
        // CompanyLookup's find-or-create reads through the owner filter, so the
        // C# side already asks the per-user question; this is the index agreeing.
        e.HasIndex(c => new { c.OwnerUserId, c.NameNormalized }).IsUnique();

        // F13 — the three columns that were unbounded `text`.
        e.Property(c => c.Website).HasMaxLength(500);
        e.Property(c => c.Industry).HasMaxLength(100);
        e.Property(c => c.HqLocation).HasMaxLength(200);

        // F7 — xmin is Postgres's own row version, so this costs zero added
        // columns. A concurrent overwrite now throws
        // DbUpdateConcurrencyException instead of silently discarding the
        // other write.
        ModelConventions.UseXmin(e);
    }
}
