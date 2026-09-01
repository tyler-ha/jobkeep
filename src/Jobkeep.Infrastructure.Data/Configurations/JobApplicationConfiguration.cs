using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Data.Configurations;

// PHASE 13.3a: lifted out of AppDbContext.OnModelCreating unchanged. The class
// moves to Jobkeep.Modules.Applications in 13.3b, where ToTable also gains its schema.
public class JobApplicationConfiguration : IEntityTypeConfiguration<JobApplication>
{
    public void Configure(EntityTypeBuilder<JobApplication> e)
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

        ModelConventions.UseXmin(e);

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
        //
        // 13.3b DROPS THIS FK: `resumes` is Documents' schema. The RESTRICT is
        // replaced by a contract check at write, through IResumeContract.GetAsync.
        e.HasOne(a => a.Resume)
            .WithMany(r => r.Applications)
            .HasForeignKey(a => a.ResumeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
