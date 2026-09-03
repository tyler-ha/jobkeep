using Jobkeep.Modules.Applications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Modules.Analytics.Persistence;

// One of the three views Applications publishes to Analytics. See
// ApplicationStatusCountConfiguration for why the mapping is owned by the
// CONSUMER while the shape and the SQL are owned by the publisher, and why
// naming the `applications` schema here is an address rather than a leak.
public class CompanyApplicationCountConfiguration : IEntityTypeConfiguration<CompanyApplicationCount>
{
    public void Configure(EntityTypeBuilder<CompanyApplicationCount> e)
    {
        e.HasNoKey().ToView("v_company_application_counts", "applications");
    }
}
