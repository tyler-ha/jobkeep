using System.Net.Http.Json;
using System.Text.Json;
using Jobkeep.Modules.Applications.Domain;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Rest;

/// <summary>
/// PHASE 9, gap 3 — GET /applications/board.
///
/// <para>
/// The board is not a page. ListApplications caps pageSize at 100 and rejects
/// above it, so the Pipeline screen was fetching up to five pages in a loop to
/// draw one screen. This read answers in one request, with a cap counted in
/// cards, and a totalCount taken BEFORE the cap so the screen can still say what
/// is missing.
/// </para>
///
/// <para>
/// What is deliberately NOT tested: the 500-card cap, which would need 501
/// applications arranged through the real create path to observe, and the
/// tiebreak that decides which cards a full board keeps — only reachable at that
/// same cap. Both are argued in GetBoard.cs. The rest of this file is about the
/// two things that are cheap to get wrong: the narrower projection, and the
/// archived rows.
/// </para>
/// </summary>
public sealed class BoardReadTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task TheWholeBoard_ComesBackInOneRequest()
    {
        await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);
        await Client.CreateApplicationAsync("Seek", "Senior Engineer", Ct);

        var board = await BoardAsync();

        Assert.Equal(3, board.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, board.GetProperty("cards").GetArrayLength());
    }

    [Fact]
    public async Task ACard_CountsItsSkills_InsteadOfNamingThem()
    {
        // The reason this read exists as its own projection rather than reusing
        // ApplicationListItem: the card shows HOW MANY skills, never which, so the
        // response carries a count and the handler skips the catalog lookup the
        // list has to make. Asserting the names are absent is the half that would
        // rot silently — a `skills` array creeping back costs a second query per
        // request and nothing on the screen would show it.
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        (await Client.AddSkillAsync(id, "C#", Ct)).EnsureSuccessStatusCode();
        (await Client.AddSkillAsync(id, "PostgreSQL", Ct)).EnsureSuccessStatusCode();

        var card = (await BoardAsync()).GetProperty("cards")[0];

        Assert.Equal(2, card.GetProperty("skillCount").GetInt32());
        Assert.False(card.TryGetProperty("skills", out _));
    }

    [Fact]
    public async Task AnArchivedApplication_IsOffTheBoard_AndOutOfItsCount()
    {
        // PHASE 8 — an archive is the thing you take OFF the board, and the global
        // query filter already does that half. The count is the half worth pinning:
        // it is taken through the same projection as the cards, so a board of one
        // cannot report a total of two and print a footer about a card that was
        // never missing.
        var archived = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);
        (await Client.DeleteAsync($"/applications/{archived}", Ct)).EnsureSuccessStatusCode();

        var board = await BoardAsync();

        Assert.Equal(1, board.GetProperty("totalCount").GetInt32());
        Assert.Equal("Atlassian",
            board.GetProperty("cards")[0].GetProperty("company").GetString());
    }

    [Fact]
    public async Task TheBoard_IsNewestFirst()
    {
        // DateApplied is not settable through the API — every application created
        // above is dated today — so this is the one test that has to reach past the
        // HTTP surface to arrange. Ordering matters because the cap keeps the head
        // of this list: a board that truncated the newest applications would be
        // showing exactly the wrong five hundred.
        var older = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);

        await WithDbAsync(async db =>
        {
            var row = await db.JobApplications.FindAsync([older], Ct);
            row!.DateApplied = row.DateApplied.AddDays(-30);
            await db.SaveChangesAsync(Ct);
        });

        var cards = (await BoardAsync()).GetProperty("cards");

        Assert.Equal("Atlassian", cards[0].GetProperty("company").GetString());
        Assert.Equal("Canva", cards[1].GetProperty("company").GetString());
    }

    [Fact]
    public async Task GraphQL_AnswersTheSameBoard()
    {
        // One rule, one implementation — both surfaces are adapters over the same
        // handler, and the field takes no arguments because the board has no
        // filter, sort or page for a caller to pass.
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        (await Client.AddSkillAsync(id, "C#", Ct)).EnsureSuccessStatusCode();

        var result = await GraphQL.QueryAsync(
            "{ applicationBoard { totalCount cards { company skillCount } } }");

        Assert.False(result.HasErrors, result.FirstErrorMessage);
        var board = result.Data!.Value.GetProperty("applicationBoard");
        Assert.Equal(1, board.GetProperty("totalCount").GetInt32());

        var card = board.GetProperty("cards")[0];
        Assert.Equal("Canva", card.GetProperty("company").GetString());
        Assert.Equal(1, card.GetProperty("skillCount").GetInt32());
    }

    private async Task<JsonElement> BoardAsync()
    {
        var response = await Client.GetAsync("/applications/board", Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct))!;
    }
}
