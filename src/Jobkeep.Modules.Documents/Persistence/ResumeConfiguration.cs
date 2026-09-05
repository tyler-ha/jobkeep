using Jobkeep.Modules.Documents.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Jobkeep.SharedKernel;

namespace Jobkeep.Modules.Documents.Persistence;

// PHASE 13.3b: moved here from Jobkeep.Infrastructure.Data alongside the
// entity it maps, and ToTable gained the `documents` schema. Everything else is
// unchanged from 13.3a, which lifted it out of AppDbContext.OnModelCreating
// verbatim.
public class ResumeConfiguration : IEntityTypeConfiguration<Resume>
{
    public void Configure(EntityTypeBuilder<Resume> e)
    {
        e.ToTable("resumes", "documents");
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
        // PHASE 8 MADE IT FILTERED, and this is the one index change soft delete
        // actually forces rather than merely suggests.
        //
        // Without the filter, archiving "backend" would keep its row in the
        // unique index forever, and the next import under that label would be
        // refused by a constraint naming a résumé the user can no longer see.
        // The label would be burned by an action the UI calls "archive". With
        // it, archiving frees the name and re-import behaves as it always has.
        //
        // The cost is real and is paid in RestoreResume: a restore can now fail,
        // because something may have taken the label in between. That is a 400
        // with a sentence, and it is the honest trade — a name the user can
        // reuse, against a restore that is conditional rather than guaranteed.
        //
        // `NOT "IsDeleted"` rather than `"IsDeleted" = false`: same predicate,
        // and Postgres records the index predicate verbatim, so this is the form
        // `pg_dump` will show and the form a future migration has to match
        // character-for-character to be seen as unchanged.
        // PHASE 11.2b — the owner joins the key, and the Phase 8 FILTER stays:
        // archiving still frees the label, and it now frees it for its owner
        // only. RestoreResume's "has someone taken this label" check reads
        // through the owner filter, so it asks the same question this index
        // answers.
        e.HasIndex(r => new { r.OwnerUserId, r.LabelNormalized })
            .IsUnique().HasFilter("NOT \"IsDeleted\"");

        // PHASE 8 — soft delete. Nothing else in this module is archivable, so
        // there is no filtered/unfiltered relationship pairing to get wrong here:
        // resume_skills, resume_experiences and resume_educations are children
        // that survive an archive by construction.
        e.HasQueryFilter(QueryFilters.SoftDelete, r => !r.IsDeleted);
    }
}
