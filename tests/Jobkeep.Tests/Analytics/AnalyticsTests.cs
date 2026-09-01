using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Analytics;

/// <summary>
/// Phase 2.4 — /stats/skill-demand, /stats/funnel, /stats/companies.
///
/// These run against real Postgres for a sharper reason than the CRUD tests do. Every
/// query here is an EF <c>GroupBy</c>, and a GroupBy is the most fragile thing to
/// translate: it either becomes a SQL <c>GROUP BY</c> or EF Core throws
/// <c>InvalidOperationException</c> — since EF Core 3.0 there is no silent fall back to
/// counting in memory. So the fact that these pass at all, against a real provider,
/// <em>is</em> the "aggregation happens in the database" check the phase doc asks for.
/// A fake repository would answer every one of them correctly with LINQ-to-Objects and
/// prove nothing.
/// </summary>
public sealed class AnalyticsTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    // ------------------------------------------------------------------
    // Skill demand — the query Postgres was chosen for
    // ------------------------------------------------------------------

    [Fact]
    public async Task SkillDemand_RanksSkillsByHowManyPostingsAskForThem()
    {
        var canva = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        var atlassian = await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);
        var seek = await Client.CreateApplicationAsync("Seek", "Data Engineer", Ct);

        // C# in three postings, AWS in two, Go in one.
        foreach (var id in new[] { canva, atlassian, seek })
            (await Client.AddSkillAsync(id, "C#", Ct)).EnsureSuccessStatusCode();
        foreach (var id in new[] { canva, atlassian })
            (await Client.AddSkillAsync(id, "AWS", Ct)).EnsureSuccessStatusCode();
        (await Client.AddSkillAsync(seek, "Go", Ct)).EnsureSuccessStatusCode();

        var demand = await SkillDemandAsync();

        Assert.Equal(
            [("C#", 3), ("AWS", 2), ("Go", 1)],
            demand.Select(s => (Name(s), Count(s))).ToArray());
    }

    [Fact]
    public async Task SkillDemand_MatchesTheRawSqlItWasSpecifiedAs()
    {
        // The phase-2 doc justified choosing Postgres with a literal psql one-liner. This
        // asserts the endpoint answers the same thing that query does, so the feature and
        // the argument that motivated it cannot quietly drift apart.
        var canva = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        var atlassian = await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);
        (await Client.AddSkillAsync(canva, "C#", Ct)).EnsureSuccessStatusCode();
        (await Client.AddSkillAsync(atlassian, "C#", Ct)).EnsureSuccessStatusCode();
        (await Client.AddSkillAsync(canva, "AWS", Ct)).EnsureSuccessStatusCode();

        var topSkillPerSql = (string?)await ScalarAsync(
            """
            SELECT s."Name" FROM skills s
            JOIN posting_skills ps ON ps."SkillId" = s."Id"
            GROUP BY s."Name" ORDER BY COUNT(*) DESC, s."Name" LIMIT 1
            """);

        var demand = await SkillDemandAsync();

        Assert.Equal("C#", topSkillPerSql);
        Assert.Equal(topSkillPerSql, Name(demand[0]));
    }

    [Fact]
    public async Task SkillDemand_CarriesTheSkillCategory()
    {
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        (await Client.AddSkillAsync(id, "C#", Ct, category: "Language")).EnsureSuccessStatusCode();

        var demand = await SkillDemandAsync();

        Assert.Equal("Language", Assert.Single(demand).GetProperty("category").GetString());
    }

    [Fact]
    public async Task SkillDemand_RespectsTop()
    {
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        foreach (var skill in new[] { "C#", "AWS", "Go" })
            (await Client.AddSkillAsync(id, skill, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(3, (await SkillDemandAsync()).Length);
        Assert.Equal(2, (await SkillDemandAsync("?top=2")).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task SkillDemand_RejectsAnOutOfRangeTop(int top)
    {
        // The cap is a denial-of-service guard on an unauthenticated surface, the same one
        // ListApplications puts on pageSize. It rejects rather than clamps, so a caller
        // can tell it did not get what it asked for.
        var response = await Client.GetAsync($"/stats/skill-demand?top={top}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("top must be between 1 and 100", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task SkillDemand_IsEmptyWhenNothingIsTracked()
    {
        Assert.Empty(await SkillDemandAsync());
    }

    [Fact]
    public async Task SkillDemand_CountsSkillsDifferingOnlyInCaseAsOne()
    {
        // PHASE 7 FLIPPED THIS TEST. It used to be named
        // `SkillDemand_SplitsSkillsDifferingOnlyInCase_WhichIsTheKnownDedupGap`
        // and asserted the defect: `skills` dedupped case-sensitively, so "C#"
        // and "c#" were two rows, and a demand ranking is precisely what a
        // duplicate row corrupts. The old test's own comment said that when the
        // natural key landed it would fail, and that the failure would be the
        // signal the fix worked. It did, and this is the same scenario asserting
        // the truth instead of the bug.
        //
        // Kept in place rather than deleted and rewritten elsewhere, so `git log`
        // on this method shows the defect and its fix in one history.
        //
        // The true answer: ONE skill, wanted by TWO postings.
        var canva = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        var atlassian = await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);
        (await Client.AddSkillAsync(canva, "C#", Ct)).EnsureSuccessStatusCode();
        (await Client.AddSkillAsync(atlassian, "c#", Ct)).EnsureSuccessStatusCode();

        var demand = await SkillDemandAsync();

        var only = Assert.Single(demand);
        Assert.Equal(2, Count(only));
    }

    [Fact]
    public async Task SkillDemand_KeepsTheFirstSpellingItSaw()
    {
        // The other half of the natural key, and the part a user notices: the
        // stored row keeps the spelling that created it. Adding "c#" second
        // resolves to the existing "C#" row rather than renaming it, so the
        // ranking reads the way the user typed it the first time.
        //
        // This is a deliberate choice, not a side effect. Letting a later write
        // restyle an existing row would mean a résumé import could silently
        // relabel a skill the user entered by hand.
        var canva = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        var atlassian = await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);
        (await Client.AddSkillAsync(canva, "PostgreSQL", Ct)).EnsureSuccessStatusCode();
        (await Client.AddSkillAsync(atlassian, "postgresql", Ct)).EnsureSuccessStatusCode();

        var only = Assert.Single(await SkillDemandAsync());
        Assert.Equal("PostgreSQL", only.GetProperty("name").GetString());
    }

    // ------------------------------------------------------------------
    // Status funnel
    // ------------------------------------------------------------------

    [Fact]
    public async Task Funnel_ListsEveryStage_IncludingTheEmptyOnes()
    {
        // The reason the zero-fill exists. A stage with no rows cannot come back from a
        // GROUP BY, and "no offers yet" is exactly the fact the view is for.
        await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        var funnel = await FunnelAsync();
        var stages = funnel.GetProperty("stages").EnumerateArray().ToArray();

        Assert.Equal(
            ["Applied", "Interviewing", "Offer", "Rejected", "Withdrawn"],
            stages.Select(s => s.GetProperty("status").GetString()).ToArray());
        Assert.Equal([1, 0, 0, 0, 0], stages.Select(Count).ToArray());
    }

    [Fact]
    public async Task Funnel_CountsByStatus_AndTotalsToEveryApplication()
    {
        var interviewing = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        var offer = await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);
        await Client.CreateApplicationAsync("Seek", "Data Engineer", Ct);
        await Client.CreateApplicationAsync("Xero", "API Engineer", Ct);

        await PatchStatusAsync(interviewing, "Interviewing");
        await PatchStatusAsync(offer, "Offer");

        var funnel = await FunnelAsync();
        var byStatus = funnel.GetProperty("stages").EnumerateArray()
            .ToDictionary(s => s.GetProperty("status").GetString()!, Count);

        Assert.Equal(2, byStatus["Applied"]);
        Assert.Equal(1, byStatus["Interviewing"]);
        Assert.Equal(1, byStatus["Offer"]);
        Assert.Equal(0, byStatus["Rejected"]);

        // The total is served rather than left to the caller to sum, so it has to agree
        // with the stages — and with the number of rows actually in the table.
        Assert.Equal(4, funnel.GetProperty("total").GetInt32());
        Assert.Equal(4, byStatus.Values.Sum());
        Assert.Equal(4L, await ScalarAsync("SELECT COUNT(*) FROM job_applications"));
    }

    // ------------------------------------------------------------------
    // Company rollup
    // ------------------------------------------------------------------

    [Fact]
    public async Task CompanyRollup_CountsApplicationsPerCompany_RankedByCount()
    {
        // "3 roles at Canva" — the sentence Company.cs uses to justify storing an employer
        // as its own row. Two applications to Canva share one company row via
        // find-or-create, which is what makes this a count of employers rather than a
        // count of duplicated strings.
        await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        await Client.CreateApplicationAsync("Canva", "Senior Backend Engineer", Ct);
        await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);

        var rollup = await GetArrayAsync("/stats/companies");

        Assert.Equal(
            [("Canva", 2), ("Atlassian", 1)],
            rollup.Select(c => (Name(c), c.GetProperty("applicationCount").GetInt32())).ToArray());

        // One company row per name, not one per application.
        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM companies"));
    }

    [Fact]
    public async Task CompanyRollup_RejectsAnOutOfRangeTop()
    {
        var response = await Client.GetAsync("/stats/companies?top=0", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------
    // Surface parity — both APIs, one handler
    // ------------------------------------------------------------------

    [Fact]
    public async Task GraphQL_ReturnsTheSameAggregatesAsRest()
    {
        var canva = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        var atlassian = await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);
        (await Client.AddSkillAsync(canva, "C#", Ct)).EnsureSuccessStatusCode();
        (await Client.AddSkillAsync(atlassian, "C#", Ct)).EnsureSuccessStatusCode();
        (await Client.AddSkillAsync(canva, "AWS", Ct)).EnsureSuccessStatusCode();
        await PatchStatusAsync(atlassian, "Interviewing");

        var graphql = await GraphQL.QueryAsync(
            """
            query {
              skillDemand(top: 5) { name postingCount }
              statusFunnel { total stages { status count } }
              companyRollup { name applicationCount }
            }
            """);

        Assert.False(graphql.HasErrors);
        var data = graphql.Data!.Value;

        var demand = data.GetProperty("skillDemand").EnumerateArray().ToArray();
        Assert.Equal(
            [("C#", 2), ("AWS", 1)],
            demand.Select(s => (Name(s), Count(s))).ToArray());

        // Enums come back SCREAMING_CASE over GraphQL and PascalCase over REST. That is
        // the one difference between the surfaces here, and it is a convention of each
        // protocol rather than a difference in behaviour.
        var funnel = data.GetProperty("statusFunnel");
        Assert.Equal(2, funnel.GetProperty("total").GetInt32());
        Assert.Equal(
            ["APPLIED", "INTERVIEWING", "OFFER", "REJECTED", "WITHDRAWN"],
            funnel.GetProperty("stages").EnumerateArray()
                .Select(s => s.GetProperty("status").GetString()).ToArray());

        Assert.Equal(2, data.GetProperty("companyRollup").GetArrayLength());
    }

    [Fact]
    public async Task GraphQL_RejectsAnOutOfRangeTop_WithTheSameRuleRestEnforces()
    {
        // Same handler, so the cap cannot mean 100 on one surface and nothing on the
        // other — which is the whole point of validating inside the slice (decision 10).
        var graphql = await GraphQL.QueryAsync("query { skillDemand(top: 101) { name } }");

        Assert.Equal("INVALID_INPUT", graphql.FirstErrorCode);
        Assert.Equal("top must be between 1 and 100.", graphql.FirstErrorMessage);
    }

    // ------------------------------------------------------------------
    // Helpers. Raw JsonElement rather than the app's own records, for the reason
    // ApiHelpers.GetApplicationAsync gives: the wire format is the contract, so the tests
    // read the wire format.
    // ------------------------------------------------------------------

    private Task<JsonElement[]> SkillDemandAsync(string query = "")
        => GetArrayAsync($"/stats/skill-demand{query}");

    private async Task<JsonElement[]> GetArrayAsync(string url)
    {
        var response = await Client.GetAsync(url, Ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private async Task<JsonElement> FunnelAsync()
    {
        var response = await Client.GetAsync("/stats/funnel", Ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.Clone();
    }

    private async Task PatchStatusAsync(Guid id, string status) =>
        (await Client.PatchAsJsonAsync($"/applications/{id}", new { status }, Ct))
            .EnsureSuccessStatusCode();

    private static string? Name(JsonElement element) => element.GetProperty("name").GetString();

    private static int Count(JsonElement element) =>
        element.TryGetProperty("count", out var count)
            ? count.GetInt32()
            : element.GetProperty("postingCount").GetInt32();
}
