using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Data.Configurations;

// PHASE 13.3a: lifted out of AppDbContext.OnModelCreating unchanged. The class
// moves to Jobkeep.Modules.Documents in 13.3b, where ToTable also gains its schema.
public class ResumeConfiguration : IEntityTypeConfiguration<Resume>
{
    public void Configure(EntityTypeBuilder<Resume> e)
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
    }
}
