using Jobkeep.Data;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Skills;

// The one implementation of ISkillCatalog. Holds ISkillsDbContext, which exposes
// exactly one DbSet — this module cannot see another module's tables even by
// accident, which is the property Phase 13.2 exists to establish.
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
}
