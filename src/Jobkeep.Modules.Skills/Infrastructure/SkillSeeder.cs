using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jobkeep.Modules.Skills;

// PHASE 14 — puts a starting vocabulary in `skills` and `skill_aliases`.
//
// WHY SEED AT ALL
// ---------------
// Without it the catalogue is whatever a 3B model happened to call things on the
// days you imported. That is how `Agile` and `Agile Methodologies` both came to
// exist: neither import was wrong, they just used different words a week apart,
// and nothing in the schema could tell they meant one thing. Seeding turns the
// common cases into decisions taken once, in a file that can be read and argued
// with, instead of accidents of extraction order.
//
// WHY A JSON FILE AND NOT `HasData`
// ---------------------------------
// EF's HasData is migration-based seeding: it wants a fixed primary key per row,
// so ~200 skills means ~200 hand-written GUIDs in source, and every edit to the
// list becomes a new migration. This list will be edited often — every ad using
// a word we have not met is a candidate — so it belongs in data, not in schema
// history. The file is an embedded resource so it ships with the assembly and
// cannot go missing in a container image.
//
// WHY IT IS SAFE TO RUN ON EVERY BOOT
// -----------------------------------
// Every write is conditional and nothing is ever overwritten. See the three
// rules in SeedAsync. The cost of a no-op run is two SELECTs.
public class SkillSeeder
{
    private readonly SkillsDbContext _db;
    private readonly ILogger<SkillSeeder> _log;

    public SkillSeeder(SkillsDbContext db, ILogger<SkillSeeder> log)
    {
        _db = db;
        _log = log;
    }

    // The file's shape. Kept internal and minimal — this is a data format, not a
    // domain model, and the moment it grows a second consumer it should stop
    // being read straight into entities.
    private sealed record SeedFile(List<SeedSkill> Skills);

    private sealed record SeedSkill(
        string Name,
        SkillKind Kind = SkillKind.Unknown,
        string? Category = null,
        List<string>? Aliases = null);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var file = Read();
        if (file is null || file.Skills.Count == 0) return;

        // Both tables in full, once. The seed touches most of the catalogue by
        // definition, so a WHERE IN over ~500 names would be a longer query than
        // the table it filters — and this runs at startup, not per request.
        var skills = await _db.Skills.ToDictionaryAsync(s => s.NameNormalized, ct);
        var aliasKeys = await _db.SkillAliases
            .Select(a => a.AliasNormalized)
            .ToListAsync(ct);

        var takenByAlias = new HashSet<string>(aliasKeys, StringComparer.Ordinal);
        var added = 0;
        var aliasesAdded = 0;
        var filled = 0;
        var skipped = 0;

        foreach (var seed in file.Skills)
        {
            var name = seed.Name.Trim();
            if (name.Length == 0) continue;

            var key = NaturalKey.Of(name);

            // RULE 1 — an existing skill is never replaced, and its Name keeps the
            // spelling that is already there. A row the user has been looking at
            // does not get renamed underneath them by a file.
            if (!skills.TryGetValue(key, out var skill))
            {
                skill = new Skill
                {
                    Name = name,
                    Kind = seed.Kind,
                    Category = string.IsNullOrWhiteSpace(seed.Category) ? null : seed.Category.Trim()
                };
                _db.Skills.Add(skill);
                skills[key] = skill;
                added++;
            }
            else
            {
                // RULE 2 — fill only what is genuinely unset. This is the same
                // "first writer names it" rule SkillRequest states, applied to the
                // seed: a Category typed by a human, or a Kind an import decided,
                // outranks the file. Unknown and null are the only two values the
                // seed is allowed to replace, because they are the two that mean
                // "nobody has said".
                if (skill.Kind == SkillKind.Unknown && seed.Kind != SkillKind.Unknown)
                {
                    skill.Kind = seed.Kind;
                    filled++;
                }

                if (skill.Category is null && !string.IsNullOrWhiteSpace(seed.Category))
                {
                    skill.Category = seed.Category.Trim();
                    filled++;
                }
            }

            foreach (var raw in seed.Aliases ?? [])
            {
                var alias = raw.Trim();
                if (alias.Length == 0) continue;

                var aliasKey = NaturalKey.Of(alias);

                // RULE 3 — an alias may not collide with a SKILL name or another
                // alias, and a collision is REPORTED, not thrown.
                //
                // The skill half is the invariant SkillAlias.cs describes: one
                // name must not resolve to two rows. It fires legitimately —
                // "Agile Methodologies" is already a skill row in a database
                // seeded before this phase, so the alias is refused and the two
                // rows stay separate until that database is dropped. That is the
                // documented cost of not merging existing duplicates, and seeing
                // it in the log is how you know which rows are affected.
                //
                // Not thrown, because reference data with one bad row must not
                // stop the app booting. A seed that half-applies and says which
                // half is strictly better than a seed that refuses to start.
                if (skills.ContainsKey(aliasKey) || !takenByAlias.Add(aliasKey))
                {
                    skipped++;
                    _log.LogWarning(
                        "Skill seed: alias {Alias} for {Skill} was skipped — that name is already " +
                        "a skill row or another alias.", alias, name);
                    continue;
                }

                // SkillId rather than a navigation property: Skill has no
                // collection to add to (13.3b removed every navigation on it) and
                // the id is set in the property initialiser, so it is available
                // before SaveChanges.
                _db.SkillAliases.Add(new SkillAlias { SkillId = skill.Id, Alias = alias });
                aliasesAdded++;
            }
        }

        if (added + aliasesAdded + filled == 0)
        {
            _log.LogInformation("Skill seed: nothing to do, {Count} skills already present.", skills.Count);
            return;
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation(
            "Skill seed: {Added} skills added, {Aliases} aliases added, {Filled} fields filled, " +
            "{Skipped} aliases skipped.", added, aliasesAdded, filled, skipped);
    }

    private SeedFile? Read()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("skills-seed.json", StringComparison.Ordinal));

        if (resource is null)
        {
            // A missing seed file is a build problem (the csproj's EmbeddedResource
            // item), not a runtime one, and the app is perfectly usable without it
            // — the catalogue just starts empty as it did before Phase 14. So this
            // warns and carries on rather than refusing to boot.
            _log.LogWarning("Skill seed: skills-seed.json is not embedded in the assembly; skipping.");
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<SeedFile>(stream, JsonOptions);
    }
}
