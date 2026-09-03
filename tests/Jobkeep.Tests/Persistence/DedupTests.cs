using System.Net;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Persistence;

/// <summary>
/// Find-or-create dedup on the two unique natural keys, companies.Name and skills.Name.
///
/// This is the reason the project chose Postgres over DynamoDB, and the reason these
/// tests run against real Postgres rather than a fake or an in-memory provider: a fake
/// repository would happily report "one company" while the actual SQL inserted two, and
/// EF's InMemory provider does not enforce unique indexes at all.
/// </summary>
public sealed class DedupTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task TwoApplications_AtTheSameCompany_ShareOneCompanyRow()
    {
        await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        await Client.CreateApplicationAsync("Canva", "Platform Engineer", Ct);

        var companies = await WithDbAsync(db =>
            db.Companies.Where(c => c.Name == "Canva").ToListAsync(Ct));
        var postings = await WithDbAsync(db => db.JobPostings.CountAsync(Ct));

        Assert.Single(companies);
        Assert.Equal(2, postings);
    }

    [Fact]
    public async Task TheSameSkill_OnTwoPostings_SharesOneSkillRowAndCreatesTwoLinks()
    {
        // This is the query the whole storage choice rests on: "top skills across all my
        // tracked jobs" is only one GROUP BY if the skills row is shared.
        var first = await Client.CreateApplicationAsync("Atlassian", "Backend Engineer", Ct);
        var second = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        (await Client.AddSkillAsync(first, "C#", Ct)).EnsureSuccessStatusCode();
        (await Client.AddSkillAsync(second, "C#", Ct)).EnsureSuccessStatusCode();

        var skills = await WithDbAsync(db => db.Skills.Where(s => s.Name == "C#").ToListAsync(Ct));
        // 13.3b: `ps.Skill.Name` was a join onto another module's table, and the
        // navigation is gone. Counting by the id the single skill row actually has
        // asserts the same thing and is what the application would have to do.
        var skillId = Assert.Single(skills).Id;
        var links = await WithDbAsync(db =>
            db.PostingSkills.Where(ps => ps.SkillId == skillId).CountAsync(Ct));

        Assert.Single(skills);
        Assert.Equal(2, links);
    }

    [Fact]
    public async Task AddingASkillToAFreshPosting_Inserts_RegressionForSetKeyMeansExisting()
    {
        // Regression guard for the bug recorded in docs/phases/phase-2-postgres.md:
        // entity Ids are initialised with Guid.NewGuid(), so EF treated a brand-new Skill
        // reached through a tracked application as an *existing* row, skipped the INSERT,
        // and the join row then failed on a foreign key. AddSkillToPosting.cs fixes it
        // with an explicit _db.Skills.Add(skill). If that line goes, this fails.
        var application = await Client.CreateApplicationAsync("Xero", "Senior Engineer", Ct);

        var response = await Client.AddSkillAsync(application, "Kubernetes", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await WithDbAsync(db => db.Skills.AnyAsync(s => s.Name == "Kubernetes", Ct)));
        Assert.Equal(1, await WithDbAsync(db => db.PostingSkills.CountAsync(Ct)));
    }

    [Fact]
    public async Task SkillLookupIsCaseInsensitive_SoCSharpAndLowercaseCSharpAreOneRow()
    {
        // FLIPPED IN PHASE 7. This used to be named
        // `SkillLookupIsCaseSensitive_SoCSharpAndLowercaseCSharpAreTwoRows` and
        // documented the defect: AddSkillToPosting matched on `s.Name == skillName`,
        // which Npgsql translates to a case-sensitive comparison, so "C#" and "c#"
        // became two rows in a table whose whole purpose is deduplication. Its own
        // comment predicted the fix — "a case-insensitive natural key ... which is a
        // schema change and belongs to its own phase" — and that is exactly what
        // landed: a STORED generated column, `lower("Name")`, carrying the unique
        // index.
        //
        // Two tests asserted this defect and both broke on the migration, which is
        // the behaviour the project wanted from writing defects down as tests: the
        // fix announces itself instead of being noticed later.
        var first = await Client.CreateApplicationAsync("Seek", "Engineer", Ct);
        var second = await Client.CreateApplicationAsync("REA Group", "Engineer", Ct);

        (await Client.AddSkillAsync(first, "C#", Ct)).EnsureSuccessStatusCode();
        (await Client.AddSkillAsync(second, "c#", Ct)).EnsureSuccessStatusCode();

        var names = await WithDbAsync(db =>
            db.Skills.Select(s => s.Name).OrderBy(n => n).ToListAsync(Ct));

        Assert.Equal("C#", Assert.Single(names));

        // And the link rows still point at that one skill from both postings —
        // the dedup is what makes /stats/skill-demand count it as one skill
        // wanted twice rather than two skills wanted once each.
        Assert.Equal(2, await WithDbAsync(db => db.PostingSkills.CountAsync(Ct)));
    }

    [Fact]
    public async Task CompanyUniqueIndex_IsEnforcedByTheDatabase_NotJustTheApplication()
    {
        // The EF InMemory provider ignores unique indexes entirely, so this assertion is
        // only meaningful against real Postgres. It proves the constraint that makes
        // find-or-create safe actually exists in the schema.
        await Client.CreateApplicationAsync("Telstra", "Engineer", Ct);

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => WithDbAsync(async db =>
        {
            db.Companies.Add(new Jobkeep.Models.Company { Name = "Telstra" });
            await db.SaveChangesAsync(Ct);
        }));

        Assert.Contains("duplicate key", failure.InnerException?.Message ?? "");
    }
}
