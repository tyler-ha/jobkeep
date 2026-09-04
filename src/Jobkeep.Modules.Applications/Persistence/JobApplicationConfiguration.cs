using Jobkeep.Contracts.Documents;
using Jobkeep.Modules.Applications.Domain;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Modules.Applications.Persistence;

// PHASE 13.3b: moved here from Jobkeep.Infrastructure.Data alongside the
// entity it maps, and ToTable gained the `applications` schema. Everything else is
// unchanged from 13.3a, which lifted it out of AppDbContext.OnModelCreating
// verbatim.
public class JobApplicationConfiguration : IEntityTypeConfiguration<JobApplication>
{
    public void Configure(EntityTypeBuilder<JobApplication> e)
    {
        e.ToTable("job_applications", "applications");
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

        ModelConventions.UseXmin(e);

        // The application points at a posting, but deleting the application
        // must NOT delete the posting (a posting can have several applications).
        e.HasOne(a => a.Posting)
            .WithMany(p => p.Applications)
            .HasForeignKey(a => a.PostingId)
            .OnDelete(DeleteBehavior.Restrict);

        // Phase 4.5 added `ResumeId` with a RESTRICT foreign key, so that
        // deleting a resume you had applied with could not silently delete the
        // applications that used it.
        //
        // 13.3b DROPPED IT: `resumes` is Documents' table in Documents' schema.
        // The write half of the guarantee is already elsewhere — CreateApplication
        // and UpdateApplication both call IResumeContract.GetAsync before storing
        // the id, and have since 13.2 — so the write path behaves identically.
        // The delete half is genuinely unprotected until 13.3c.
        //
        // Worth being precise about what was lost, because "we replaced the FK
        // with a contract check" is only two thirds true: a check at write cannot
        // stop a row disappearing afterwards. What actually replaces RESTRICT is
        // a rule Documents has to enforce on its own delete, which is the price
        // of the boundary rather than an oversight.
    }
}
