using Jobkeep.Models;
using Jobkeep.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Modules.Documents.Persistence;

// PHASE 13.3b: moved here from Jobkeep.Infrastructure.Data alongside the
// entity it maps, and ToTable gained the `documents` schema. Everything else is
// unchanged from 13.3a, which lifted it out of AppDbContext.OnModelCreating
// verbatim.
public class DocumentImportConfiguration : IEntityTypeConfiguration<DocumentImport>
{
    public void Configure(EntityTypeBuilder<DocumentImport> e)
    {
        e.ToTable("document_imports", "documents");
        e.Property(d => d.Kind).HasConversion<string>().HasMaxLength(20);
        e.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
        e.Property(d => d.Format).HasConversion<string>().HasMaxLength(20);
        e.Property(d => d.FileName).HasMaxLength(260);
        e.Property(d => d.ContentHash).HasMaxLength(64);   // SHA-256 as hex

        // jsonb, not text: Postgres validates the structure on write and the
        // column is inspectable with -> in psql when a draft looks wrong.
        // See DocumentImport.DraftJson for why the draft is a document rather
        // than five mirror tables.
        e.Property(d => d.DraftJson).HasColumnType("jsonb");

        // The review queue is the only way this table is ever read in bulk
        // ("what am I still to confirm"), so it gets the index and nothing
        // else does. Note this is a filtered index on an enum stored as
        // text — the comparison is against the string name, not the int.
        e.HasIndex(d => d.Status);
    }
}
