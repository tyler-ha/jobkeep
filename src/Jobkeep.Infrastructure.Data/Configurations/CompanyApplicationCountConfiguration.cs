using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Data.Configurations;

// PHASE 13.3a: lifted out of AppDbContext.OnModelCreating unchanged.
//
// Phase 13.2 — one of the three views Applications publishes to Analytics.
// HasNoKey + ToView: EF reads them and will never try to write them, and it
// leaves them out of the migration model entirely — the CREATE VIEW statements
// are hand-written in the AnalyticsViews migration, because a view is not
// something EF scaffolds.
//
// The configuration belongs to APPLICATIONS, not Analytics, because Applications
// publishes it. That is the whole point of the shape: the owner decides what it
// exposes. At 13.3b it moves into the `applications` schema with the tables it
// reads.
public class CompanyApplicationCountConfiguration : IEntityTypeConfiguration<CompanyApplicationCount>
{
    public void Configure(EntityTypeBuilder<CompanyApplicationCount> e)
    {
        e.HasNoKey().ToView("v_company_application_counts");
    }
}
