using Jobkeep.Contracts.Applications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Modules.Analytics.Persistence;

// ---------------------------------------------------------------------------
// PHASE 13.3b — why a mapping OWNED BY ANALYTICS names Applications' schema
// ---------------------------------------------------------------------------
// This is the one comment 13.3a wrote that had to be rewritten rather than
// moved, so the reversal is recorded rather than quietly performed.
//
// 13.3a said the configuration "belongs to APPLICATIONS, because Applications
// publishes it". That was the right instinct and the wrong half of the object.
// It could not survive the split: AnalyticsDbContext is the context that reads
// these views, a context can only apply its OWN assembly's configurations, and
// Analytics may not reference Applications. Leaving the mapping on the
// publishing side would have left Analytics unable to read the three questions
// it exists to answer.
//
// The line that does survive is publisher-owns-the-definition,
// consumer-owns-the-read. The payload SHAPE is Applications' published interface
// and lives in Jobkeep.Contracts; the SQL is Applications' initial migration and
// nobody else can change it; the READ — HasNoKey, ToView, and the column-level
// conversions below — is Analytics' problem, because reading is what Analytics
// does.
//
// So naming the `applications` schema here is not a leak. That schema is the
// view's published ADDRESS, and it is exactly the string that becomes a URL when
// Analytics is lifted out. A consumer has to know where to look; what it must not
// know is what is behind the address, and it does not.
//
// HasNoKey + ToView also means EF never writes these and leaves them out of the
// migration model entirely — the CREATE VIEW statements are hand-written, because
// a view is not something EF scaffolds.
public class ApplicationStatusCountConfiguration : IEntityTypeConfiguration<ApplicationStatusCount>
{
    public void Configure(EntityTypeBuilder<ApplicationStatusCount> e)
    {
        e.HasNoKey().ToView("v_application_status_counts", "applications");
        // The underlying column is text (HasConversion<string> on
        // JobApplication.Status), so the view's column is text too and the
        // same conversion has to be declared on the way back in.
        e.Property(x => x.Status).HasConversion<string>();
    }
}
