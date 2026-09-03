using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jobkeep.Persistence;

// The mapping rules that are true of every entity, lifted out of
// AppDbContext.OnModelCreating in Phase 13.3a so that the six contexts arriving
// in 13.3b can each apply them instead of each re-stating them.
//
// Nothing here changed when it moved. The comments are the originals, because
// they are the argument for the rule and that is the part that is not
// recoverable from the code.
public static class ModelConventions
{
    // Phase 7, F7 — optimistic concurrency via Postgres's own `xmin` system
    // column, which every row already has. Zero added columns and no schema
    // change on the table itself: EF maps it as a shadow property, reads it
    // with the row, and adds it to the UPDATE's WHERE clause. A second write
    // against a stale copy then matches no rows and EF raises
    // DbUpdateConcurrencyException instead of silently discarding the first.
    //
    // Written out rather than using the provider's old
    // `UseXminAsConcurrencyToken()` helper, which was REMOVED in the Npgsql
    // 7 provider — this is the shape its own migration guidance replaced it
    // with. Applied to the three tables with a read-modify-write update
    // path; link and child rows are insert-or-delete and have no lost-update
    // to lose.
    //
    // Opt-in per entity rather than swept over the whole model, and that is
    // deliberate: the three tables that want it are the three a user edits
    // twice. A convention that gave every link row a concurrency token would
    // be enforcing a rule nobody asked for on rows that cannot break it.
    public static void UseXmin<T>(EntityTypeBuilder<T> e) where T : class
        => e.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

    // -------------------------------------------------------------------
    // Phase 7, F11 — database-side defaults, applied as a convention
    // -------------------------------------------------------------------
    // Before this the schema had NO defaults at all: every id and every
    // timestamp originated in a C# property initialiser. That is fine while
    // this application is the only writer and wrong the moment anything else
    // is — a migration backfill, a `psql` fix, a future extracted service.
    // Such a writer could insert a row with a null timestamp or a zero GUID
    // and the schema would not notice, because the invariant lived in a
    // language the database does not speak.
    //
    // Applied in a loop rather than as twenty repeated lines for the same
    // reason F8 got an interceptor: a rule that must hold for every entity
    // should not depend on remembering it for every entity. A new table
    // picks this up by existing.
    //
    // Note these defaults are almost never exercised in normal operation —
    // EF always sends a value, so Postgres never falls back. That is the
    // point. They are the floor under everyone who is not EF.
    //
    // PHASE 13.3a — call this LAST, after every configuration has been
    // applied. It reads the finished model, so an entity configured after it
    // runs simply does not get the defaults, silently. Each context's
    // OnModelCreating ends with it for that reason.
    public static void ApplyDatabaseDefaults(ModelBuilder model)
    {
        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetDeclaredProperties())
            {
                // gen_random_uuid() is built in from Postgres 13; no pgcrypto
                // extension needed, which matters because Neon's free tier is
                // the deploy target and an extension is one more thing to
                // provision.
                if (property.ClrType == typeof(Guid) && property.IsPrimaryKey())
                    property.SetDefaultValueSql("gen_random_uuid()");

                // The audit pair only. Domain timestamps that mean something
                // more specific — AnalyzedAtUtc, CheckedAtUtc, CommittedAtUtc —
                // are set when that thing happened, not when the row was
                // written, so a default would be actively misleading.
                if (property.ClrType == typeof(DateTime)
                    && property.Name is "CreatedAtUtc" or "UpdatedAtUtc")
                    property.SetDefaultValueSql("now() at time zone 'utc'");
            }
        }
    }
}
