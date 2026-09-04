using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jobkeep.Contracts.Shared;
using Jobkeep.Modules.Applications.Domain;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Rest;

/// <summary>
/// PHASE 9, gap 2 — <c>ApplicationQuery.Status</c> takes a SET, and
/// <c>isClosed</c> is shorthand for the closed stages.
///
/// <para>
/// Two thirds of this file is about a claim rather than a feature: **widening one
/// value to a list breaks neither surface.** REST binds a repeated query parameter
/// to an array; GraphQL coerces a single value to a list of one, per the spec's
/// input-coercion rule for list types. Both are the kind of thing that is true
/// until a serializer option disagrees, and neither had a test — the REST side was
/// covered by accident (<c>ListApplicationsTests.Filter_ByStatus</c> kept passing),
/// the GraphQL side by nothing at all, because the only GraphQL status test in the
/// suite is on <c>updateApplication</c>'s input.
/// </para>
///
/// <para>
/// The rest is the closed set, which is a DOMAIN fact rather than a query option —
/// <see cref="ApplicationStatusTransitions.Closed"/> owns it, and the last test
/// here pins it to the transition rule that has depended on it since Phase 2.5.
/// </para>
/// </summary>
public sealed class StatusSetFilterTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    // ------------------------------------------------------------------
    // The widening breaks nothing
    // ------------------------------------------------------------------

    [Fact]
    public async Task ASingleStatus_StillFiltersOverREST_AsAOneElementArray()
    {
        await SeedAsync(("Canva", ApplicationStatus.Interviewing), ("Atlassian", null));

        var page = await ListAsync("status=Interviewing");

        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ASingleStatus_StillFiltersOverGraphQL_BecauseTheSpecCoercesItToAList()
    {
        // The compatibility claim, and the one that had no test. `status` is
        // `[ApplicationStatus!]` now; this query sends a bare enum value and must
        // still work, unedited, exactly as a shipped client would send it.
        await SeedAsync(("Canva", ApplicationStatus.Interviewing), ("Atlassian", null));

        var result = await GraphQL.QueryAsync(
            "{ applications(query: { status: INTERVIEWING }) { totalCount } }");

        Assert.False(result.HasErrors, result.FirstErrorMessage);
        Assert.Equal(1, result.Data!.Value
            .GetProperty("applications").GetProperty("totalCount").GetInt32());
    }

    // ------------------------------------------------------------------
    // The set, which is the point
    // ------------------------------------------------------------------

    [Fact]
    public async Task TwoStatuses_OverREST_ArePagedAsOneResultSet()
    {
        // The thing a union of two requests could not do honestly: one totalCount,
        // one page. `?status=A&status=B` is the repeated-parameter form.
        await SeedAsync(
            ("Canva", ApplicationStatus.Rejected),
            ("Atlassian", ApplicationStatus.Withdrawn),
            ("Seek", ApplicationStatus.Interviewing),
            ("REA Group", null));

        var page = await ListAsync("status=Rejected&status=Withdrawn");

        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            new[] { "Atlassian", "Canva" },
            Companies(page));
    }

    [Fact]
    public async Task TwoStatuses_OverGraphQL_AgreeWithREST()
    {
        await SeedAsync(
            ("Canva", ApplicationStatus.Rejected),
            ("Atlassian", ApplicationStatus.Withdrawn),
            ("Seek", ApplicationStatus.Interviewing));

        var result = await GraphQL.QueryAsync(
            "{ applications(query: { status: [REJECTED, WITHDRAWN] }) { totalCount } }");

        Assert.False(result.HasErrors, result.FirstErrorMessage);
        Assert.Equal(2, result.Data!.Value
            .GetProperty("applications").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task AnEmptyStatusList_MeansNoFilter_OverGraphQL_AndIsUnreachableOverREST()
    {
        // THE SURFACES GENUINELY DIFFER HERE, and the first version of this test got
        // it wrong by assuming they did not.
        //
        //   * GraphQL can send `status: []` — a well-formed list of zero enum values.
        //     It must mean the same as omitting the filter, not "match nothing".
        //   * REST cannot produce an empty array at all. `?status=` binds an empty
        //     string to an ApplicationStatus, fails model binding, and answers 400 —
        //     identical to `?status=Banana`, which is the correct answer.
        //
        // That is not a parity break: the two surfaces agree on every input BOTH can
        // express. REST simply has no spelling for this one.
        await SeedAsync(("Canva", ApplicationStatus.Rejected), ("Atlassian", null));

        var result = await GraphQL.QueryAsync(
            "{ applications(query: { status: [] }) { totalCount } }");

        Assert.False(result.HasErrors, result.FirstErrorMessage);
        Assert.Equal(2, result.Data!.Value
            .GetProperty("applications").GetProperty("totalCount").GetInt32());

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await Client.GetAsync("/applications?status=", Ct)).StatusCode);
    }

    // ------------------------------------------------------------------
    // isClosed
    // ------------------------------------------------------------------

    [Fact]
    public async Task IsClosed_SelectsTheClosedStages_AndItsNegationSelectsTheActiveOnes()
    {
        await SeedAsync(
            ("Canva", ApplicationStatus.Rejected),
            ("Atlassian", ApplicationStatus.Withdrawn),
            ("Seek", ApplicationStatus.Interviewing),
            ("REA Group", null));   // Applied

        Assert.Equal(2, (await ListAsync("isClosed=true")).GetProperty("totalCount").GetInt32());
        Assert.Equal(2, (await ListAsync("isClosed=false")).GetProperty("totalCount").GetInt32());

        // And it agrees with naming the same stages by hand, which is what makes it
        // shorthand rather than a second opinion.
        Assert.Equal(
            Companies(await ListAsync("isClosed=true")),
            Companies(await ListAsync("status=Rejected&status=Withdrawn")));
    }

    [Fact]
    public async Task IsClosed_WorksOverGraphQLToo()
    {
        await SeedAsync(
            ("Canva", ApplicationStatus.Rejected),
            ("Seek", ApplicationStatus.Interviewing));

        var result = await GraphQL.QueryAsync(
            "{ applications(query: { isClosed: true }) { totalCount } }");

        Assert.False(result.HasErrors, result.FirstErrorMessage);
        Assert.Equal(1, result.Data!.Value
            .GetProperty("applications").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task PassingBothStatusAndIsClosed_IsRefusedOnBothSurfaces()
    {
        // Refused rather than resolved: intersecting them answers a question nobody
        // asked, and letting one win silently means a caller who sent both never
        // learns which. Parity matters here more than usual, because a refusal is
        // exactly the kind of rule a surface can forget to enforce (finding A4).
        await SeedAsync(("Canva", ApplicationStatus.Rejected));

        var rest = await Client.GetAsync("/applications?status=Rejected&isClosed=true", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, rest.StatusCode);
        Assert.Contains("not both", await rest.Content.ReadAsStringAsync(Ct));

        var graphql = await GraphQL.QueryAsync(
            "{ applications(query: { status: REJECTED, isClosed: true }) { totalCount } }");
        Assert.True(graphql.HasErrors);
        Assert.Contains("not both", graphql.FirstErrorMessage ?? "");
    }

    // ------------------------------------------------------------------
    // The domain set, pinned to the rule that depends on it
    // ------------------------------------------------------------------

    [Fact]
    public void TheClosedStages_AreExactlyTheOnesThatCannotReachAnOffer()
    {
        // A plain unit test, and the second one in the suite — same exemption
        // ApplicationStatusTransitionTests has, for the same reason: this is a pure
        // function of two enums with no database in it.
        //
        // It is what lets ApplicationStatusTransitions.Closed be a named set WITHOUT
        // the transition table being rewritten to derive from it. The table stays
        // stage-by-stage and reviewable; this asserts the two say the same thing.
        // Phase 2.5's header states the invariant in prose — "an Offer can only be
        // reached from an active application" — and until now nothing checked it.
        var cannotReachOffer = Enum.GetValues<ApplicationStatus>()
            .Where(s => s != ApplicationStatus.Offer)
            .Where(s => !ApplicationStatusTransitions.IsAllowed(s, ApplicationStatus.Offer))
            .ToHashSet();

        Assert.Equal(
            ApplicationStatusTransitions.Closed.OrderBy(s => s),
            cannotReachOffer.OrderBy(s => s));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Create applications, PATCHing any that need a status other than Applied.</summary>
    private async Task SeedAsync(params (string Company, ApplicationStatus? Status)[] rows)
    {
        foreach (var (company, status) in rows)
        {
            var id = await Client.CreateApplicationAsync(company, "Engineer", Ct);
            if (status is null) continue;

            (await Client.PatchAsJsonAsync(
                $"/applications/{id}", new { status = status.ToString() }, Ct))
                .EnsureSuccessStatusCode();
        }
    }

    private async Task<JsonElement> ListAsync(string queryString)
    {
        var response = await Client.GetAsync($"/applications?{queryString}", Ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.Clone();
    }

    private static string[] Companies(JsonElement page) =>
        page.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("company").GetString()!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
}
