using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jobkeep.Contracts.Skills;
using Jobkeep.Modules.Applications;
using Jobkeep.Modules.Applications.Domain;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Rest;

/// <summary>
/// GET /applications — the Phase 2.3 query surface: filter, sort, page.
///
/// These run against real Postgres for a specific reason. Every filter here is an EF
/// expression that has to <em>translate</em>: EF.Functions.ILike has no meaning outside
/// Npgsql, a collection Any() has to become an EXISTS rather than a client-side scan,
/// and a projection into a DTO either becomes a column list or silently loads the world.
/// A fake repository would report every one of these green while the SQL was wrong or
/// absent — which is the whole argument Phase 2.2 made for Testcontainers.
/// </summary>
public sealed class ListApplicationsTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    // ------------------------------------------------------------------
    // Filtering
    // ------------------------------------------------------------------

    [Fact]
    public async Task Filter_ByStatus_ReturnsOnlyThatStatus()
    {
        var interviewing = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);
        (await Client.PatchAsJsonAsync($"/applications/{interviewing}", new { status = "Interviewing" }, Ct))
            .EnsureSuccessStatusCode();

        var page = await ListAsync("status=Interviewing");

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("Canva", Assert.Single(page.Items).GetProperty("company").GetString());
    }

    [Theory]
    [InlineData("Canva")]
    [InlineData("canva")]
    [InlineData("CANVA")]
    [InlineData("anv")]
    public async Task Filter_ByCompany_IsCaseInsensitiveAndMatchesAnywhere(string term)
    {
        // ILIKE, not ==. Worth four cases rather than one: the substring case proves the
        // % wrapping, and the casing cases prove it is ILIKE rather than LIKE — a
        // distinction that only exists once real Postgres runs the query.
        await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);

        var page = await ListAsync($"company={Uri.EscapeDataString(term)}");

        Assert.Equal("Canva", Assert.Single(page.Items).GetProperty("company").GetString());
    }

    [Fact]
    public async Task Filter_ByTitle_MatchesASubstringCaseInsensitively()
    {
        await Client.CreateApplicationAsync("Canva", "Senior Backend Engineer", Ct);
        await Client.CreateApplicationAsync("Atlassian", "Product Manager", Ct);

        var page = await ListAsync("title=backend");

        Assert.Equal("Senior Backend Engineer", Assert.Single(page.Items).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Filter_BySkill_IsTheJoinThatJustifiedChoosingPostgres()
    {
        // The phase's thesis, executable: "which of my applications wants C#" is one
        // EXISTS through posting_skills into the shared skills table. In a denormalized
        // store the same question means reading every document and filtering in code.
        var withCSharp = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        var withGo = await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);
        await Client.CreateApplicationAsync("Seek", "Data Engineer", Ct);

        (await Client.AddSkillAsync(withCSharp, "C#", Ct)).EnsureSuccessStatusCode();
        (await Client.AddSkillAsync(withGo, "Go", Ct)).EnsureSuccessStatusCode();

        var page = await ListAsync("skill=C%23");

        Assert.Equal(1, page.TotalCount);
        var item = Assert.Single(page.Items);
        Assert.Equal(withCSharp, item.GetProperty("id").GetGuid());
        Assert.Equal("C#", Assert.Single(item.GetProperty("skills").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Filter_BySkill_MatchesTheWholeNameNotAPrefix()
    {
        // Deliberately exact (case-insensitively), unlike company and title. A skill list
        // is full of names that are prefixes of each other — C, C#, C++ — so a contains
        // match would make "C" return all three and the filter useless.
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        (await Client.AddSkillAsync(id, "C#", Ct)).EnsureSuccessStatusCode();

        Assert.Empty((await ListAsync("skill=C")).Items);
        Assert.Single((await ListAsync("skill=c%23")).Items);
    }

    /// <summary>
    /// Phase 13.2d. The skill filter stopped being a join into <c>skills</c> and became
    /// a name lookup through <c>ISkillCatalog</c> followed by an EXISTS on the id — the
    /// shape that still works when the taxonomy is another service.
    ///
    /// <para>
    /// This is the branch that only exists because of that change: a name no row has
    /// ever carried resolves to nothing, and the filter has to mean "no results" rather
    /// than "no filter". Getting it wrong returns every application, which looks like a
    /// working page and is the opposite of what was asked.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Filter_ByASkillNobodyHasRecorded_ReturnsNothing_NotEverything()
    {
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        (await Client.AddSkillAsync(id, "C#", Ct)).EnsureSuccessStatusCode();
        await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);

        var page = await ListAsync("skill=COBOL");

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    /// <summary>
    /// Phase 13.2d. A card's skill chips are resolved from ids after the page comes
    /// back, so their order is this handler's decision rather than whatever order
    /// Postgres returned the join rows in. Alphabetical, and stable between requests —
    /// which a list of cards should be, and which the SQL version was not.
    /// </summary>
    [Fact]
    public async Task ListItems_NameTheirSkills_InAStableAlphabeticalOrder()
    {
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        foreach (var name in new[] { "Rust", "AWS", "postgresql" })
            (await Client.AddSkillAsync(id, name, Ct)).EnsureSuccessStatusCode();

        var page = await ListAsync("company=Canva");

        Assert.Equal(
            ["AWS", "postgresql", "Rust"],
            Assert.Single(page.Items).GetProperty("skills").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Filter_ByCompany_TreatsUnderscoreAsALiteral_NotAWildcard()
    {
        // _ is a single-character ILIKE wildcard, so an unescaped search for "A_lassian"
        // would match "Atlassian". The handler escapes % and _ before they reach the
        // pattern; without that this test returns a row.
        await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);

        Assert.Empty((await ListAsync("company=A_lassian")).Items);
        Assert.Empty((await ListAsync("company=%25")).Items);
    }

    [Fact]
    public async Task Filter_ByAppliedDateRange_IsInclusiveAtBothEnds()
    {
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        var today = await WithDbAsync(db => db.JobApplications
            .Where(a => a.Id == id).Select(a => a.DateApplied).FirstAsync(Ct));

        Assert.Single((await ListAsync($"appliedFrom={today:yyyy-MM-dd}")).Items);
        Assert.Single((await ListAsync($"appliedTo={today:yyyy-MM-dd}")).Items);
        Assert.Empty((await ListAsync($"appliedFrom={today.AddDays(1):yyyy-MM-dd}")).Items);
        Assert.Empty((await ListAsync($"appliedTo={today.AddDays(-1):yyyy-MM-dd}")).Items);
    }

    [Fact]
    public async Task Filters_Combine_AsAnd_NotOr()
    {
        var match = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        await Client.CreateApplicationAsync("Canva", "Product Manager", Ct);
        await Client.CreateApplicationAsync("Atlassian", "Backend Engineer", Ct);

        var page = await ListAsync("company=Canva&title=Backend");

        Assert.Equal(match, Assert.Single(page.Items).GetProperty("id").GetGuid());
    }

    // ------------------------------------------------------------------
    // Sorting and paging
    // ------------------------------------------------------------------

    [Fact]
    public async Task Sort_ByCompany_RespectsDirection()
    {
        await Client.CreateApplicationAsync("Xero", "Engineer", Ct);
        await Client.CreateApplicationAsync("Atlassian", "Engineer", Ct);
        await Client.CreateApplicationAsync("Canva", "Engineer", Ct);

        Assert.Equal(
            new[] { "Atlassian", "Canva", "Xero" },
            (await ListAsync("sort=Company&direction=Asc")).Companies());
        Assert.Equal(
            new[] { "Xero", "Canva", "Atlassian" },
            (await ListAsync("sort=Company&direction=Desc")).Companies());
    }

    [Fact]
    public async Task Sort_ByTitle_RespectsDirection()
    {
        await Client.CreateApplicationAsync("Canva", "Platform Engineer", Ct);
        await Client.CreateApplicationAsync("Atlassian", "Backend Engineer", Ct);

        var page = await ListAsync("sort=Title&direction=Asc");
        Assert.Equal(new[] { "Backend Engineer", "Platform Engineer" }, page.Field("title"));
    }

    [Fact]
    public async Task Paging_SplitsTheResultSet_AndTotalCountIgnoresThePage()
    {
        foreach (var company in new[] { "Atlassian", "Canva", "Seek", "Xero" })
        {
            await Client.CreateApplicationAsync(company, "Engineer", Ct);
        }

        var first = await ListAsync("sort=Company&direction=Asc&page=1&pageSize=2");
        var second = await ListAsync("sort=Company&direction=Asc&page=2&pageSize=2");

        Assert.Equal(new[] { "Atlassian", "Canva" }, first.Companies());
        Assert.Equal(new[] { "Seek", "Xero" }, second.Companies());

        // totalCount describes the filter's whole result set, not the page — that is
        // what lets a client render "1-2 of 4" and compute totalPages.
        Assert.Equal(4, first.TotalCount);
        Assert.Equal(4, second.TotalCount);
        Assert.Equal(2, first.TotalPages);
    }

    [Fact]
    public async Task Paging_IsStableAcrossPages_EvenWhenEveryRowTiesOnTheSortColumn()
    {
        // The reason ThenBy(a => a.Id) is in the handler. DateApplied is a DateOnly, so
        // rows logged on the same day tie on the default sort — and OFFSET over a
        // non-deterministic ORDER BY may hand back the same row on two pages while
        // another is never returned at all. Phase 2.2 met this tie from the other side
        // and had to drop an ordering assertion as flaky; the tiebreak is what makes it
        // assertable. Reading every page one row at a time must yield every row exactly
        // once.
        for (var i = 0; i < 5; i++)
        {
            await Client.CreateApplicationAsync($"Company {i}", "Engineer", Ct);
        }

        var seen = new List<Guid>();
        for (var page = 1; page <= 5; page++)
        {
            var result = await ListAsync($"page={page}&pageSize=1");
            seen.Add(Assert.Single(result.Items).GetProperty("id").GetGuid());
        }

        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public async Task Paging_PastTheEnd_IsAnEmptyPage_NotAnError()
    {
        await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        var page = await ListAsync("page=99&pageSize=20");

        Assert.Empty(page.Items);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(99, page.Page);
    }

    [Fact]
    public async Task Defaults_AreNewestFirst_WithATwentyRowPage()
    {
        await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        var page = await ListAsync(string.Empty);

        Assert.Equal(1, page.Page);
        Assert.Equal(20, page.PageSize);
    }

    // ------------------------------------------------------------------
    // Validation — rejected, not clamped
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("page=0", "page must be 1 or greater.")]
    [InlineData("page=-1", "page must be 1 or greater.")]
    [InlineData("pageSize=0", "pageSize must be between 1 and 100.")]
    [InlineData("pageSize=101", "pageSize must be between 1 and 100.")]
    [InlineData("appliedFrom=2026-12-01&appliedTo=2026-01-01", "appliedFrom must not be after appliedTo.")]
    public async Task InvalidQuery_Returns400_RatherThanSilentlyCorrectingIt(
        string queryString, string message)
    {
        // Clamping would hand the caller a page they did not ask for with no way to tell.
        // The pageSize ceiling is also the only thing standing between an unauthenticated
        // GET and `?pageSize=1000000`.
        var response = await Client.GetAsync($"/applications?{queryString}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal($"\"{message}\"", await response.Content.ReadAsStringAsync(Ct));
    }

    // ------------------------------------------------------------------
    // The projection — architecture.md A1
    // ------------------------------------------------------------------

    [Fact]
    public async Task AListItem_CarriesTheSummaryFieldsOnly_AndNotTheWholeAggregate()
    {
        // This is what pins the A1 fix. The retired repository answered every read with a
        // five-part include graph behind AsSplitQuery — company, skills, requirements, AI
        // analysis and match result — whatever the caller wanted. A list row now projects
        // to named columns, and this test fails the moment someone reintroduces an eager
        // load, because the extra properties would reappear on the wire.
        var id = await Client.CreateApplicationAsync(
            "Canva", "Backend Engineer", Ct,
            location: "Melbourne",
            description: "A very long job ad nobody wants in a list row.");
        (await Client.AddSkillAsync(id, "C#", Ct)).EnsureSuccessStatusCode();

        var item = Assert.Single((await ListAsync(string.Empty)).Items);

        Assert.Equal(
            // "isArchived" joined the list in Phase 8. It is a flag, not a payload — the
            // reason a list item carries it is in ApplicationListItem — so it does not
            // reopen A1, and this assertion staying exhaustive is what proves that.
            new[] { "company", "dateApplied", "id", "isArchived", "location", "skills", "status", "title" },
            item.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        // Named explicitly because these two are the ones that matter: the ad text is
        // large and the résumé is personal data. Neither belongs in a list nobody asked
        // for it in.
        Assert.False(item.TryGetProperty("description", out _));
        Assert.False(item.TryGetProperty("posting", out _));

        // `resumeText` used to be named here too, as the other column that must
        // never appear in a list row. Phase 4.5 deleted the column outright —
        // the résumé moved to its own table and an application now carries only
        // a ResumeId — so the assertion has nothing left to guard. The property
        // list asserted above is the stronger check anyway: it is exhaustive, so
        // a résumé field reappearing under any name fails it.
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// The paged envelope, read as raw JSON. Not bound to Jobkeep's own ApplicationPage
    /// record on purpose: deserializing into the app's type would prove the app can
    /// round-trip itself, not that the field names a client reads are the ones on the
    /// wire. The wire format is the contract.
    /// </summary>
    private sealed class PageResponse(JsonDocument document)
    {
        // Clone(): a JsonElement is a window onto its JsonDocument's pooled buffer, so
        // holding one after the document is disposed reads whatever was recycled into
        // that memory. Cloning detaches each item, which is what lets ListAsync dispose
        // the document instead of leaking one per call.
        public IReadOnlyList<JsonElement> Items { get; } =
            document.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.Clone()).ToList();

        public int TotalCount { get; } = document.RootElement.GetProperty("totalCount").GetInt32();
        public int Page { get; } = document.RootElement.GetProperty("page").GetInt32();
        public int PageSize { get; } = document.RootElement.GetProperty("pageSize").GetInt32();
        public int TotalPages { get; } = document.RootElement.GetProperty("totalPages").GetInt32();

        public string[] Companies() => Field("company");

        public string[] Field(string name) =>
            Items.Select(i => i.GetProperty(name).GetString() ?? "").ToArray();
    }

    private async Task<PageResponse> ListAsync(string queryString)
    {
        var response = await Client.GetAsync(
            queryString.Length == 0 ? "/applications" : $"/applications?{queryString}", Ct);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return new PageResponse(document);
    }
}
