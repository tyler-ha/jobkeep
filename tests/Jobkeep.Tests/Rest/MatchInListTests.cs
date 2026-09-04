using System.Text.Json;
using Jobkeep.Modules.Match.Domain;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Rest;

/// <summary>
/// PHASE 9, gap 1 — the match summary on a list row.
///
/// <para>
/// The interesting thing here is not the fraction, it is WHERE the fraction comes
/// from. <c>match_results</c> is the Match module's table and Applications cannot
/// see it, so the list handler asks <c>IMatchContract.GetSummariesAsync</c> once
/// for the whole page. The plan said this was "a change to one projection
/// expression"; Phase 13 reversed the decision that made that true, and these
/// tests are the shape the contract call produces.
/// </para>
///
/// <para>
/// Results are seeded directly rather than run through the real check, for the
/// asymmetry MatchCheckTests already argues: producing one through its own surface
/// needs a résumé, an ad with skills, and a faked model reply, all of which are
/// covered there. What THIS needs from a match result is its three arrays.
/// </para>
/// </summary>
public sealed class MatchInListTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    /// <summary>Stores a result against an application, with the three counted lists.</summary>
    private Task SeedResultAsync(
        Guid applicationId, string[] matched, string[] mustHave, string[] niceToHave) =>
        WithDbAsync(async db =>
        {
            db.MatchResults.Add(new MatchResult
            {
                ApplicationId = applicationId,
                MatchedKeywords = [.. matched],
                MissingMustHaveKeywords = [.. mustHave],
                MissingNiceToHaveKeywords = [.. niceToHave],
            });
            await db.SaveChangesAsync(Ct);
        });

    private async Task<JsonElement> ListAsync()
    {
        var response = await Client.GetAsync("/applications", Ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
    }

    [Fact]
    public async Task AListRow_CarriesTheStoredCheck_AsMatchedOverTotal()
    {
        // Two matched, one must-have missing, one nice-to-have missing: 2 of 4.
        // Asserted as the pair rather than as a string, because the fraction is the
        // client's formatting and the two integers are the contract.
        //
        // The nice-to-have is the half a total could plausibly be written without —
        // "how many of the REQUIRED skills does the CV have" is a defensible other
        // answer, and it is not this one. Including it is what makes the denominator
        // "every skill the ad named", which is what the column's header claims.
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        await SeedResultAsync(id, ["C#", "PostgreSQL"], ["Kubernetes"], ["Terraform"]);

        var row = (await ListAsync()).GetProperty("items")[0];
        var match = row.GetProperty("match");

        Assert.Equal(2, match.GetProperty("matched").GetInt32());
        Assert.Equal(4, match.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task AnApplicationNobodyHasChecked_SendsNull_NotAZeroFraction()
    {
        // The common case, and the one that fails quietly. A missing property or an
        // absent row rendered as 0/0 both look like "checked, and it matched
        // nothing" — which is a different sentence from "never checked", and the one
        // the screen would be lying with.
        await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);

        var row = (await ListAsync()).GetProperty("items")[0];

        Assert.True(row.TryGetProperty("match", out var match));
        Assert.Equal(JsonValueKind.Null, match.ValueKind);
    }

    [Fact]
    public async Task OneCheckedRowAndOneNot_ComeBackOnTheSamePage()
    {
        // The batching, from the outside. GetSummariesAsync is one call for the whole
        // page keyed by application id, so the failure this guards against is the
        // lookup missing: every row sharing the first row's summary, or every row
        // losing it. One of each on one page is the cheapest arrangement that fails
        // if the dictionary is keyed or read wrongly.
        var checkedId = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);
        await SeedResultAsync(checkedId, ["C#"], [], ["Docker"]);

        var items = (await ListAsync()).GetProperty("items").EnumerateArray().ToArray();
        var checkedRow = items.Single(i => i.GetProperty("id").GetGuid() == checkedId);
        var uncheckedRow = items.Single(i => i.GetProperty("id").GetGuid() != checkedId);

        Assert.Equal(1, checkedRow.GetProperty("match").GetProperty("matched").GetInt32());
        Assert.Equal(2, checkedRow.GetProperty("match").GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, uncheckedRow.GetProperty("match").ValueKind);
    }

    [Fact]
    public async Task GraphQL_ReportsTheSameFraction()
    {
        // Parity, and it is not free here: the summary reaches GraphQL as a nested
        // object type HotChocolate generated from the Contracts record, so this also
        // proves MatchSummary made it into the schema at all. A field that does not
        // exist is a query error, not a null.
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        await SeedResultAsync(id, ["C#", "PostgreSQL"], ["Kubernetes"], []);

        var result = await GraphQL.QueryAsync(
            "{ applications { items { id match { matched total } } } }");

        Assert.False(result.HasErrors);
        var match = result.Data!.Value
            .GetProperty("applications").GetProperty("items")[0].GetProperty("match");

        Assert.Equal(2, match.GetProperty("matched").GetInt32());
        Assert.Equal(3, match.GetProperty("total").GetInt32());
        Assert.Equal(id, result.Data!.Value
            .GetProperty("applications").GetProperty("items")[0].GetProperty("id").GetGuid());
    }
}
