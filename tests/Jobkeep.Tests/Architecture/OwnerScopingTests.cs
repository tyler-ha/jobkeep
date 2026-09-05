using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Architecture;

/// <summary>
/// PHASE 11.2b — the verification the phase exists to make: a second user
/// cannot reach the first's rows, through either surface.
/// </summary>
/// <remarks>
/// <para>
/// 11.2a asked <i>is anyone there</i> and <see cref="AuthorizationTests"/>
/// guards it. This asks <i>whose row is this</i>, and it is a different kind of
/// test: there is no metadata to inspect, because the answer is a WHERE clause
/// that either ran or did not. So every case here goes over the wire as a second
/// person and looks at what comes back.
/// </para>
/// <para>
/// BOTH SURFACES, deliberately. F5 was a GraphQL-only exposure that REST never
/// had — the two surfaces share slices now, but "share" is a property of the
/// code as it stands, not a guarantee, and a resolver added next year that
/// reaches a context directly would be invisible to a REST-only test.
/// </para>
/// </remarks>
public sealed class OwnerScopingTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    /// <summary>The second person. Signed in, authorized, and owns nothing.</summary>
    private HttpClient OtherUser()
    {
        var client = Fixture.App.CreateClient();
        client.AsTestUser(TestAuthHandler.OtherUserId);
        return client;
    }

    [Fact]
    public async Task AnotherUsersApplication_IsNotInTheirList_AndIsNotFetchableById()
    {
        var mine = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        using var other = OtherUser();

        var list = await other.GetFromJsonAsync<JsonElement>("/applications", Ct);
        Assert.Equal(0, list.GetProperty("items").GetArrayLength());

        // 404, not 403. The row is not hidden from them, it does not exist for
        // them — a 403 would confirm that an application with this id is out
        // there, which is a fact they are not entitled to either.
        var byId = await other.GetAsync($"/applications/{mine}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
    }

    [Fact]
    public async Task AnotherUsersApplication_IsInvisibleOverGraphQL_Too()
    {
        var mine = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        using var other = OtherUser();
        var graphql = new GraphQLClient(other);

        var page = await graphql.QueryAsync("query { applications { totalCount items { id } } }");
        Assert.False(page.HasErrors);
        Assert.Equal(0, page.Data!.Value.GetProperty("applications").GetProperty("totalCount").GetInt32());

        var one = await graphql.QueryAsync(
            "query($id: UUID!) { application(id: $id) { id } }", new { id = mine });
        Assert.True(one.HasErrors);
    }

    [Fact]
    public async Task AChildRow_IsUnreachableThroughAnotherUsersParent()
    {
        // The claim IOwned makes about the five child tables: they carry no owner
        // column because every slice that touches one resolves its parent first,
        // and that read is filtered. This is that claim, executed.
        var mine = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        using var other = OtherUser();

        var skill = await other.AddSkillAsync(mine, "C#", Ct);
        Assert.Equal(HttpStatusCode.NotFound, skill.StatusCode);

        var requirement = await other.AddRequirementAsync(mine, "Five years of C#", Ct);
        Assert.Equal(HttpStatusCode.NotFound, requirement.StatusCode);
    }

    [Fact]
    public async Task IncludingArchivedRows_DoesNotAlsoIncludeAnotherUsers()
    {
        // The one that would have shipped broken. Before the filters were named,
        // ?includeArchived=true called a bare IgnoreQueryFilters(), which drops
        // EVERY filter — so asking to see your own archive would have handed you
        // everyone's live rows as well.
        var mine = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        (await Client.DeleteAsync($"/applications/{mine}", Ct)).EnsureSuccessStatusCode();

        using var other = OtherUser();

        var list = await other.GetFromJsonAsync<JsonElement>(
            "/applications?includeArchived=true", Ct);

        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Insights_AreCountedPerUser_BecauseTheViewsGroupByOwner()
    {
        // The half a query filter cannot reach. All three /stats reads go through
        // Postgres views, which are SQL the ORM never sees — Phase 8 hit this and
        // had to re-cut the same three by hand.
        var mine = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        (await Client.AddSkillAsync(mine, "C#", Ct)).EnsureSuccessStatusCode();

        using var other = OtherUser();

        var funnel = await other.GetFromJsonAsync<JsonElement>("/stats/funnel", Ct);
        Assert.Equal(0, funnel.GetProperty("total").GetInt32());

        var companies = await other.GetFromJsonAsync<JsonElement>("/stats/companies", Ct);
        Assert.Equal(0, companies.GetArrayLength());

        var demand = await other.GetFromJsonAsync<JsonElement>("/stats/skill-demand", Ct);
        Assert.Equal(0, demand.GetArrayLength());
    }

    [Fact]
    public async Task TwoPeople_CanBothApplyToTheSameCompany()
    {
        // companies.NameNormalized was globally unique, so the second person to
        // apply at Canva would have been refused by an index over a row they
        // cannot see — a 500 with no explanation available to them. The unique
        // index carries the owner now, and this is what that buys.
        await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        using var other = OtherUser();

        // CreateApplicationAsync throws on a non-2xx, so reaching the assertion
        // is most of the test: before the index carried the owner, this POST was
        // a 500 from a duplicate key.
        await other.CreateApplicationAsync("Canva", "Platform Engineer", Ct);

        var theirs = await other.GetFromJsonAsync<JsonElement>("/applications", Ct);
        var mine = await Client.GetFromJsonAsync<JsonElement>("/applications", Ct);

        Assert.Equal(1, theirs.GetProperty("items").GetArrayLength());
        Assert.Equal(1, mine.GetProperty("items").GetArrayLength());
        Assert.Equal("Canva", theirs.GetProperty("items")[0].GetProperty("company").GetString());
    }
}
