using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Skills;

// The one implementation of ISkillCatalog. Holds SkillsDbContext, which exposes
// only this module's own tables — it cannot see another module's even by
// accident, which is the property Phase 13.2 exists to establish. (Two tables
// since Phase 14: `skills` and `skill_aliases`. The second is invisible through
// the interface, which is the point of resolving names in here.)
//
// It is also the only place in `src/` that calls NaturalKey.Of on a skill name.
// Phase 7 shipped that rule as "every writer must remember"; 13.2c makes it "no
// writer can forget", which is the difference between a convention and a design.
public class SkillCatalog : ISkillCatalog
{
    private readonly SkillsDbContext _db;

    public SkillCatalog(SkillsDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<Guid, SkillInfo>> GetAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        // Guard the empty case rather than letting it become `WHERE Id = ANY('{}')`.
        // It is a correct query that returns nothing, and it is still a round trip
        // for a caller that already knows the answer.
        if (ids.Count == 0) return new Dictionary<Guid, SkillInfo>();

        // Distinct first: a page of posting_skills rows will name the same skill
        // for several postings, and the parameter list is what goes over the wire.
        var wanted = ids.Distinct().ToList();

        return await _db.Skills
            .AsNoTracking()
            .Where(s => wanted.Contains(s.Id))
            .Select(s => new SkillInfo(s.Id, s.Name, s.Category, s.Kind))
            .ToDictionaryAsync(s => s.Id, ct);
    }

    public async Task<SkillInfo?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        // AsNoTracking, unlike FindOrCreateAsync below: this one never writes, and
        // the caller gets a DTO it cannot accidentally mutate into an UPDATE.
        var key = NaturalKey.Of(trimmed);
        var direct = await _db.Skills
            .AsNoTracking()
            .Where(s => s.NameNormalized == key)
            .Select(s => new SkillInfo(s.Id, s.Name, s.Category, s.Kind))
            .FirstOrDefaultAsync(ct);

        if (direct is not null) return direct;

        // PHASE 14 — the alias leg, and it runs SECOND for a reason worth stating
        // once here rather than in three places. A real skill row always beats an
        // alias, so if the invariant "no alias shares a natural key with a skill"
        // is ever broken by hand, the row the user can see in `skills` wins and
        // the stray alias does nothing. SkillSeeder is what normally keeps that
        // from happening; this ordering is what makes it not matter when it does.
        //
        // A second round trip only on a miss. The hit path — the common one once
        // the vocabulary has warmed up — is unchanged and still one query.
        return await _db.SkillAliases
            .AsNoTracking()
            .Where(a => a.AliasNormalized == key)
            .Join(_db.Skills, a => a.SkillId, s => s.Id, (_, s) => s)
            .Select(s => new SkillInfo(s.Id, s.Name, s.Category, s.Kind))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, SkillInfo>> FindOrCreateAsync(
        IReadOnlyCollection<SkillRequest> skills, CancellationToken ct = default)
    {
        var result = new Dictionary<string, SkillInfo>(StringComparer.Ordinal);

        // Clean the input here rather than making four callers each do it. Blank
        // names are dropped (see the interface); the surviving names keep their
        // original spelling, because that is what the caller will look the result
        // up by and it is what the user typed or the model found.
        var wanted = skills
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => s with { Name = s.Name.Trim() })
            .ToList();

        if (wanted.Count == 0) return result;

        // One query for the whole batch, then decide in memory. A per-name round
        // trip would be one SELECT and one INSERT per skill, and the callers
        // arrive with a document's worth of them.
        var keys = wanted.Select(s => NaturalKey.Of(s.Name)).Distinct().ToList();
        var existing = await _db.Skills
            .Where(s => keys.Contains(s.NameNormalized))
            .ToDictionaryAsync(s => s.NameNormalized, ct);

        // PHASE 14 — the alias leg, batched like the one above and asked ONLY
        // about the names that missed.
        //
        // Ordering first, because it is the same rule FindByNameAsync states: a
        // real skill row beats an alias, always. Restricting the query to the
        // misses is what makes that structural here rather than a matter of which
        // dictionary is consulted first — a key that matched a skill is never
        // even asked about.
        //
        // It also keeps the warm path at one round trip. Once the vocabulary has
        // settled, most batches match `skills` outright and this query never runs.
        var missed = keys.Where(k => !existing.ContainsKey(k)).ToList();
        var aliased = missed.Count == 0
            ? new Dictionary<string, Skill>(StringComparer.Ordinal)
            : await _db.SkillAliases
                .Where(a => missed.Contains(a.AliasNormalized))
                .Join(_db.Skills, a => a.SkillId, s => s.Id,
                      (a, s) => new { a.AliasNormalized, Skill = s })
                .ToDictionaryAsync(x => x.AliasNormalized, x => x.Skill, ct);

        var created = false;

        foreach (var request in wanted)
        {
            var key = NaturalKey.Of(request.Name);

            // Skill, then alias, then create. `aliased` holds the CANONICAL row,
            // so an alias hit puts the caller's spelling in `result` pointing at
            // the real skill — which is exactly what the caller wants and cannot
            // work out for itself.
            if (!existing.TryGetValue(key, out var skill)
                && !aliased.TryGetValue(key, out skill))
            {
                // Added explicitly: Skill.Id is client-generated in the property
                // initializer, so EF reads the set key as "already exists" and
                // skips the INSERT unless told. Getting this wrong breaks the
                // caller's foreign key, not this method.
                skill = new Skill
                {
                    Name = request.Name,
                    Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim(),

                    // Advisory on create, exactly like Category above and for the
                    // same reason — see SkillRequest. A caller that does not know
                    // passes Unknown, which is a real answer rather than a gap.
                    Kind = request.Kind,
                };
                _db.Skills.Add(skill);

                // Into `existing` as well as the result, so a batch carrying "C#"
                // and "c#" creates ONE row and links both spellings to it. First
                // spelling in the caller's list wins, which keeps the one the user
                // can actually see in their document.
                existing[key] = skill;
                created = true;
            }

            // Keyed by the name as passed in, including its original casing, so a
            // caller holding "c#" can find what it asked for.
            result[request.Name] = new SkillInfo(skill.Id, skill.Name, skill.Category, skill.Kind);
        }

        // Only when something is actually new. A batch that matched every name is
        // the common case once the taxonomy has warmed up, and it should not cost
        // a write round trip to discover that.
        if (created) await _db.SaveChangesAsync(ct);

        return result;
    }
}
