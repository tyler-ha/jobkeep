using Jobkeep.Models;
using Jobkeep.Modules.Skills;
using Jobkeep.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Tests.Skills;

/// <summary>
/// Phase 14 — the alias resolution in <c>SkillCatalog</c> and the idempotency of
/// <c>SkillSeeder</c>.
///
/// <para>
/// These resolve <c>ISkillCatalog</c> out of the running host rather than driving
/// an HTTP route, and that is not a retreat from the project's
/// integration-over-unit rule — it is the rule applied honestly. The Skills module
/// has no surface of its own by design (see <c>SkillsModule.cs</c>), so there is no
/// endpoint to drive; what these tests need from a real environment is a real
/// Postgres, because the whole mechanism under test is a STORED generated column
/// and a unique index on it. A fake catalog would agree with itself about
/// <c>lower()</c> and prove nothing.
/// </para>
///
/// <para>
/// Every test arranges its own rows. The seeded vocabulary is loaded once when the
/// host boots and then truncated by Respawn before each test, so nothing here may
/// assume a seeded row exists — which is the right dependency anyway: a test that
/// broke when someone edited <c>skills-seed.json</c> would be testing the data, not
/// the code.
/// </para>
/// </summary>
public class SkillVocabularyTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private async Task<T> WithCatalogAsync<T>(Func<ISkillCatalog, Task<T>> work)
    {
        using var scope = Fixture.App.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<ISkillCatalog>());
    }

    /// <summary>Arrange one canonical skill plus the alternative spellings it answers to.</summary>
    private Task<Guid> SeedSkillAsync(
        string name, SkillKind kind = SkillKind.Technical, string? category = null,
        params string[] aliases)
        => WithDbAsync(async db =>
        {
            var skill = new Skill { Name = name, Kind = kind, Category = category };
            db.Skills.Add(skill);
            foreach (var alias in aliases)
                db.SkillAliases.Add(new SkillAlias { SkillId = skill.Id, Alias = alias });
            await db.SaveChangesAsync(Ct);
            return skill.Id;
        });

    // -----------------------------------------------------------------------
    // Resolution
    // -----------------------------------------------------------------------

    /// <summary>
    /// The defect that motivated the phase, stated as a test: the dev database held
    /// <c>Agile</c> AND <c>Agile Methodologies</c> as separate rows, so one skill's
    /// demand count was split in two and the ATS check read the difference as a gap.
    /// </summary>
    [Fact]
    public async Task Find_or_create_resolves_an_alias_to_the_canonical_row()
    {
        var agile = await SeedSkillAsync("Agile", SkillKind.Technical, "Practice", "Agile Methodologies");

        var resolved = await WithCatalogAsync(c =>
            c.FindOrCreateAsync([new SkillRequest("Agile Methodologies")], Ct));

        var info = Assert.Single(resolved).Value;
        Assert.Equal(agile, info.Id);

        // The CANONICAL name comes back, not the spelling that was asked for. A
        // caller handed its own wording back could not tell two names were one skill.
        Assert.Equal("Agile", info.Name);
        Assert.Equal("Practice", info.Category);

        // And no second row was created — which is the whole point, and the thing a
        // response-shape assertion alone would not prove.
        var count = await WithDbAsync(db => db.Skills.CountAsync(Ct));
        Assert.Equal(1, count);
    }

    /// <summary>
    /// Both spellings in one batch collapse onto one row, exactly as two casings
    /// already did. This is the shape a real import arrives in — a document naming
    /// Docker in the summary and "Docker Containers" in a bullet point.
    /// </summary>
    [Fact]
    public async Task Two_spellings_in_one_batch_resolve_to_one_row()
    {
        var docker = await SeedSkillAsync("Docker", SkillKind.Technical, "Containers", "Docker Containers");

        var resolved = await WithCatalogAsync(c => c.FindOrCreateAsync(
            [new SkillRequest("Docker"), new SkillRequest("Docker Containers")], Ct));

        // Two keys, because the dictionary is keyed by the name as passed in...
        Assert.Equal(2, resolved.Count);
        // ...pointing at one row, which is what the callers' DistinctBy collapses.
        Assert.Single(resolved.Values.DistinctBy(s => s.Id));
        Assert.All(resolved.Values, s => Assert.Equal(docker, s.Id));
    }

    /// <summary>
    /// Aliases go through the same natural key as names, so case does not matter on
    /// either side of the lookup.
    /// </summary>
    [Fact]
    public async Task Find_by_name_resolves_an_alias_case_insensitively()
    {
        var docker = await SeedSkillAsync("Docker", SkillKind.Technical, null, "Docker Containers");

        var found = await WithCatalogAsync(c => c.FindByNameAsync("dOcKeR cOnTaInErS", Ct));

        Assert.NotNull(found);
        Assert.Equal(docker, found.Id);
        Assert.Equal("Docker", found.Name);
    }

    /// <summary>
    /// The ordering rule from <c>SkillAlias.cs</c>, asserted rather than trusted: a
    /// real skill row beats an alias carrying the same natural key.
    ///
    /// <para>
    /// The seeder refuses to create this state, so the only way to reach it is by
    /// hand — which is exactly why the catalog is written to survive it. Arranged
    /// directly here because no application path can produce it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_real_skill_beats_an_alias_with_the_same_name()
    {
        var real = await SeedSkillAsync("Containers", SkillKind.Technical);
        await SeedSkillAsync("Docker", SkillKind.Technical, null, "Containers");

        var found = await WithCatalogAsync(c => c.FindByNameAsync("Containers", Ct));

        Assert.NotNull(found);
        Assert.Equal(real, found.Id);
        Assert.Equal("Containers", found.Name);
    }

    /// <summary>
    /// The catalogue is a vocabulary that grows, not a whitelist. A name the seed has
    /// never heard of still gets a row — refusing it would mean a user cannot record
    /// something real because a JSON file is out of date.
    /// </summary>
    [Fact]
    public async Task An_unknown_name_still_creates_a_row_with_kind_unknown()
    {
        var resolved = await WithCatalogAsync(c =>
            c.FindOrCreateAsync([new SkillRequest("Obscure Homegrown Framework")], Ct));

        var info = Assert.Single(resolved).Value;
        Assert.Equal(SkillKind.Unknown, info.Kind);

        var stored = await WithDbAsync(db =>
            db.Skills.SingleAsync(s => s.Id == info.Id, Ct));
        Assert.Equal("Obscure Homegrown Framework", stored.Name);
        Assert.Equal(SkillKind.Unknown, stored.Kind);
    }

    /// <summary>
    /// Kind is advisory on CREATE and never on update — the same rule Category has
    /// carried since 13.2. Two imports disagreeing must not take turns overwriting
    /// each other.
    /// </summary>
    [Fact]
    public async Task Kind_is_set_on_create_and_never_overwritten()
    {
        await WithCatalogAsync(c => c.FindOrCreateAsync(
            [new SkillRequest("Mentoring", Kind: SkillKind.Soft)], Ct));

        // A second caller insisting it is Technical does not get to relabel it.
        var second = await WithCatalogAsync(c => c.FindOrCreateAsync(
            [new SkillRequest("Mentoring", Kind: SkillKind.Technical)], Ct));

        Assert.Equal(SkillKind.Soft, Assert.Single(second).Value.Kind);
    }

    /// <summary>
    /// The enum reaches Postgres as text, not as an ordinal. Only the raw column
    /// value proves it: EF reads a HasConversion&lt;string&gt;() property back as a
    /// CLR enum either way, so a round-trip assertion would pass on an integer column.
    /// </summary>
    [Fact]
    public async Task Kind_is_stored_as_text()
    {
        await SeedSkillAsync("Communication", SkillKind.Soft, "Interpersonal");

        var stored = await ScalarAsync(
            "SELECT \"Kind\" FROM skills.skills WHERE \"Name\" = 'Communication'");

        Assert.Equal("Soft", stored);
    }

    // -----------------------------------------------------------------------
    // The seeder
    // -----------------------------------------------------------------------

    private async Task RunSeederAsync()
    {
        using var scope = Fixture.App.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SkillSeeder>().SeedAsync(Ct);
    }

    /// <summary>
    /// It runs on every boot, so running it twice must not double the catalogue. This
    /// is the property that lets the seed file be edited and the app restarted rather
    /// than migrated.
    /// </summary>
    [Fact]
    public async Task The_seeder_is_idempotent()
    {
        await RunSeederAsync();
        var (skills, aliases) = await CountsAsync();

        // A non-trivial catalogue, so "idempotent" is not vacuously true because
        // nothing was inserted in the first place.
        Assert.True(skills > 100, $"expected the seed file to carry a real vocabulary, got {skills}");
        Assert.True(aliases > 100, $"expected the seed file to carry real aliases, got {aliases}");

        await RunSeederAsync();

        Assert.Equal((skills, aliases), await CountsAsync());
    }

    /// <summary>
    /// Both kinds are present. The whole complaint that started this phase was that
    /// extraction only ever produced technologies, so a seed with no soft skills in it
    /// would ship the same gap in a different place.
    /// </summary>
    [Fact]
    public async Task The_seed_carries_both_kinds()
    {
        await RunSeederAsync();

        var byKind = await WithDbAsync(db => db.Skills
            .GroupBy(s => s.Kind)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, Ct));

        Assert.True(byKind.GetValueOrDefault(SkillKind.Technical) > 100);
        Assert.True(byKind.GetValueOrDefault(SkillKind.Soft) > 30);

        // Nothing in the file should be left uncategorised — an Unknown row in the
        // seed is a line someone forgot to finish.
        Assert.Equal(0, byKind.GetValueOrDefault(SkillKind.Unknown));
    }

    /// <summary>
    /// A row a human has categorised outranks the file. "First writer names it"
    /// applies to the seed too, which is why it fills only null and Unknown.
    /// </summary>
    [Fact]
    public async Task The_seeder_does_not_overwrite_what_is_already_there()
    {
        // Deliberately disagreeing with the seed file, which calls C# a Language.
        await SeedSkillAsync("C#", SkillKind.Soft, "My Own Category");

        await RunSeederAsync();

        var stored = await WithDbAsync(db =>
            db.Skills.SingleAsync(s => s.NameNormalized == "c#", Ct));

        Assert.Equal("My Own Category", stored.Category);
        Assert.Equal(SkillKind.Soft, stored.Kind);
    }

    /// <summary>
    /// The gap-filling half of the same rule: Unknown and null mean "nobody has
    /// said", so the seed is allowed to answer.
    /// </summary>
    [Fact]
    public async Task The_seeder_fills_a_kind_nobody_has_set()
    {
        await WithDbAsync(async db =>
        {
            db.Skills.Add(new Skill { Name = "Kubernetes" });   // Kind defaults to Unknown
            await db.SaveChangesAsync(Ct);
        });

        await RunSeederAsync();

        var stored = await WithDbAsync(db =>
            db.Skills.SingleAsync(s => s.NameNormalized == "kubernetes", Ct));

        Assert.Equal(SkillKind.Technical, stored.Kind);
        Assert.Equal("Containers", stored.Category);
    }

    /// <summary>
    /// The invariant, at the place that enforces it: an alias may not share a natural
    /// key with a skill row. This is the case a pre-Phase-14 database actually
    /// presents — "Agile Methodologies" already exists as its own row — and the
    /// seeder must skip that alias and carry on rather than throwing on startup.
    /// </summary>
    [Fact]
    public async Task An_alias_colliding_with_an_existing_skill_is_skipped_not_thrown()
    {
        await SeedSkillAsync("Agile Methodologies");

        await RunSeederAsync();   // must not throw

        var aliasExists = await WithDbAsync(db =>
            db.SkillAliases.AnyAsync(a => a.AliasNormalized == "agile methodologies", Ct));

        Assert.False(aliasExists);

        // Both rows survive, separately. That is the documented cost of not merging
        // duplicates that predate the phase — recorded here so the behaviour is a
        // decision rather than a surprise.
        var names = await WithDbAsync(db => db.Skills
            .Where(s => s.NameNormalized == "agile" || s.NameNormalized == "agile methodologies")
            .CountAsync(Ct));
        Assert.Equal(2, names);
    }

    /// <summary>
    /// No alias in the shipped file may collide with any skill name in it. The test
    /// above proves the seeder survives a collision; this one proves the data does
    /// not contain one, which is a different claim and the one that decides whether
    /// the vocabulary actually works out of the box.
    /// </summary>
    [Fact]
    public async Task The_seed_file_contains_no_alias_that_shadows_a_skill()
    {
        await RunSeederAsync();

        var shadowed = await WithDbAsync(db => db.SkillAliases
            .Where(a => db.Skills.Any(s => s.NameNormalized == a.AliasNormalized))
            .Select(a => a.Alias)
            .ToListAsync(Ct));

        Assert.Empty(shadowed);
    }

    private Task<(int Skills, int Aliases)> CountsAsync() => WithDbAsync(async db =>
        (await db.Skills.CountAsync(Ct), await db.SkillAliases.CountAsync(Ct)));
}
