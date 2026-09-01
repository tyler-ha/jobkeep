using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Find-or-create a company by name, shared by the CreateApplication and
// UpdateApplication slices.
//
// `companies.Name` carries a unique index, and that index is the point: one
// company row backs every posting and application for that employer, which is
// what makes "3 roles at Canva" a group rather than a string comparison. So the
// name has to be resolved to an existing row *before* the insert, or the insert
// trips the index.
//
// Two slices needing the same lookup is not the repository coming back. The
// difference is what the code owns: the retired IJobApplicationRepository owned
// the queries themselves, and every use case had to be expressed as one of its
// methods. This owns one lookup, called by slices that still write their own
// queries — and when a third slice needs something different, it writes its own
// query rather than growing this file.
//
// Phase 7 closed the known limit this file used to carry. The match was
// case-sensitive, so "Canva" and "canva" were two companies with one rollup
// each. It now resolves against `NameNormalized`, the STORED generated column
// Postgres computes as lower("Name"), which is also where the unique index
// lives — so the lookup and the constraint agree by construction rather than by
// both remembering to call ToLower().
internal static class CompanyLookup
{
    public static async Task<Company> ResolveAsync(
        AppDbContext db, Company incoming, CancellationToken ct = default)
    {
        // Normalise in C# with the same rule Postgres uses for the generated
        // column. EF translates this to a plain equality against an indexed
        // column, so it is an index seek — unlike `c.Name.ToLower() == ...`,
        // which would have to compute lower() for every row it scanned.
        var key = NaturalKey.Of(incoming.Name);
        var existing = await db.Companies.FirstOrDefaultAsync(c => c.NameNormalized == key, ct);

        // Not found: hand back the new instance so the caller inserts it as part
        // of whatever graph it is building.
        if (existing is null) return incoming;

        // Found: reuse the tracked row, and let the caller fill in optional
        // detail it happens to know that the stored row is missing. ??= means an
        // existing value is never overwritten by a blank one.
        existing.Website ??= incoming.Website;
        existing.Industry ??= incoming.Industry;
        existing.HqLocation ??= incoming.HqLocation;
        return existing;
    }

    public static Task<Company> ResolveAsync(AppDbContext db, string name, CancellationToken ct = default)
        => ResolveAsync(db, new Company { Name = name }, ct);

}
