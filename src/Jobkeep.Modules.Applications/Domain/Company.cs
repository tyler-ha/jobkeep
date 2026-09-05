using System.Text.Json.Serialization;
using Jobkeep.Persistence;
using Jobkeep.SharedKernel;

namespace Jobkeep.Modules.Applications.Domain;

// An employer. Kept as its own row (unique Name) so multiple postings and
// applications can share one company instead of duplicating it — that's what
// enables company-level rollups like "3 roles at Canva".
public class Company : IAuditable, IOwned
{
    // PHASE 11.2b — the owner. Stamped once, on insert, by
    // AuditSaveChangesInterceptor; never assigned by a slice, and never sent by
    // a client. Enforced on read by the `Owner` global query filter in
    // ApplicationsDbContext. See IOwned for why the children do not carry it.
    public Guid OwnerUserId { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;   // unique
    public string? Website { get; set; }
    public string? Industry { get; set; }
    public string? HqLocation { get; set; }

    // Phase 7 — the case-insensitive natural key. A STORED generated column:
    // Postgres computes lower("Name") on write, so it cannot drift from the
    // value it normalises and no C# writer can forget to set it. The unique
    // index lives on THIS column, not on Name, which is what makes "Canva"
    // and "canva" one row instead of two.
    public string NameNormalized { get; private set; } = string.Empty;

    // Phase 7 — maintained by AuditSaveChangesInterceptor, never by hand.
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;


    // Back-reference — ignored in REST JSON to avoid cycles / [null] noise.
    [JsonIgnore] public List<JobPosting> Postings { get; set; } = new();
}
