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
        var links = await WithDbAsync(db =>
            db.PostingSkills.Where(ps => ps.Skill.Name == "C#").CountAsync(Ct));

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
    public async Task SkillLookupIsCaseSensitive_SoCSharpAndLowercaseCSharpAreTwoRows()
    {
        // Documents current behaviour, and it is a finding rather than a preference:
        // AddSkillToPosting matches on `s.Name == skillName`, which Npgsql translates to
        // a case-sensitive comparison. So "C#" and "c#" become two rows in a table whose
        // whole purpose is deduplication, and skill-demand analytics will double-count
        // them. Fixing it means a case-insensitive natural key (a citext column or a
        // normalised name), which is a schema change and belongs to its own phase.
        var first = await Client.CreateApplicationAsync("Seek", "Engineer", Ct);
        var second = await Client.CreateApplicationAsync("REA Group", "Engineer", Ct);

        (await Client.AddSkillAsync(first, "C#", Ct)).EnsureSuccessStatusCode();
        (await Client.AddSkillAsync(second, "c#", Ct)).EnsureSuccessStatusCode();

        var names = await WithDbAsync(db =>
            db.Skills.Select(s => s.Name).OrderBy(n => n).ToListAsync(Ct));

        Assert.Equal(2, names.Count);
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
