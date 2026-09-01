using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Skills;

// The one implementation of ISkillCatalog. Holds ISkillsDbContext, which exposes
// exactly one DbSet — this module cannot see another module's tables even by
// accident, which is the property Phase 13.2 exists to establish.
//
// It is also the only place in `src/` that calls NaturalKey.Of on a skill name.
// Phase 7 shipped that rule as "every writer must remember"; 13.2c makes it "no
// writer can forget", which is the difference between a convention and a design.
public class SkillCatalog : ISkillCatalog
{
    private readonly ISkillsDbContext _db;

    public SkillCatalog(ISkillsDbContext db) => _db = db;

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
            .Select(s => new SkillInfo(s.Id, s.Name, s.Category))
            .ToDictionaryAsync(s => s.Id, ct);
    }

    public async Task<SkillInfo?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        // AsNoTracking, unlike FindOrCreateAsync below: this one never writes, and
        // the caller gets a DTO it cannot accidentally mutate into an UPDATE.
        var key = NaturalKey.Of(trimmed);
        return await _db.Skills
            .AsNoTracking()
            .Where(s => s.NameNormalized == key)
            .Select(s => new SkillInfo(s.Id, s.Name, s.Category))
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

        var created = false;

        foreach (var request in wanted)
        {
            var key = NaturalKey.Of(request.Name);

            if (!existing.TryGetValue(key, out var skill))
            {
                // Added explicitly: Skill.Id is client-generated in the property
                // initializer, so EF reads the set key as "already exists" and
                // skips the INSERT unless told. Getting this wrong breaks the
                // caller's foreign key, not this method.
                skill = new Skill
                {
                    Name = request.Name,
                    Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim(),
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
            result[request.Name] = new SkillInfo(skill.Id, skill.Name, skill.Category);
        }

        // Only when something is actually new. A batch that matched every name is
        // the common case once the taxonomy has warmed up, and it should not cost
        // a write round trip to discover that.
        if (created) await _db.SaveChangesAsync(ct);

        return result;
    }
}
