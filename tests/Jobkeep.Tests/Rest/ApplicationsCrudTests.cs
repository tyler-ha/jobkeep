using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Rest;

/// <summary>
/// The Phase 2 create/read/update/delete routes in Endpoints/ApplicationEndpoints.cs.
///
/// Status codes differ across the two route groups that share the /applications prefix,
/// so they are asserted exactly rather than via IsSuccessStatusCode.
/// </summary>
public sealed class ApplicationsCrudTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Create_Returns201_WithALocationHeaderPointingAtTheNewApplication()
    {
        var response = await Client.PostAsJsonAsync("/applications", new
        {
            company = "Canva",
            title = "Backend Engineer",
            notes = "Applied via referral",
        }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var id = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal($"/applications/{id}", response.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("", "Backend Engineer")]
    [InlineData("   ", "Backend Engineer")]
    [InlineData("Canva", "")]
    [InlineData("Canva", "   ")]
    public async Task Create_WithBlankCompanyOrTitle_Returns400WithABareJsonString(
        string company, string title)
    {
        var response = await Client.PostAsJsonAsync("/applications", new { company, title }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Not ProblemDetails: Results.BadRequest(string) serialises the message as a bare
        // JSON string, quotes included. Asserting the exact shape keeps a future move to
        // ProblemDetails from silently changing the contract without failing a test.
        Assert.Equal("\"Company and Title are required.\"", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task GetById_ForAnUnknownId_Returns404WithAnEmptyBody()
    {
        var response = await Client.GetAsync($"/applications/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task GetById_WithANonGuidId_Returns404BecauseTheRouteConstraintDoesNotMatch()
    {
        // The {id:guid} constraint means a malformed id never reaches the handler.
        var response = await Client.GetAsync("/applications/not-a-guid", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_SerialisesEnumsByName_NotByInteger()
    {
        var id = await Client.CreateApplicationAsync("Atlassian", "Backend Engineer", Ct);

        using var application = await Client.GetApplicationAsync(id, Ct);
        var status = application.RootElement.GetProperty("status");
        var employmentType = application.RootElement
            .GetProperty("posting").GetProperty("employmentType");

        Assert.Equal(JsonValueKind.String, status.ValueKind);
        Assert.Equal("Applied", status.GetString());
        Assert.Equal("FullTime", employmentType.GetString());
    }

    [Fact]
    public async Task Patch_UpdatesOnlyTheFieldsSupplied_AndLeavesTheRestAlone()
    {
        var id = await Client.CreateApplicationAsync(
            "Xero", "Senior Engineer", Ct, notes: "original notes");

        var response = await Client.PatchAsJsonAsync(
            $"/applications/{id}", new { status = "Interviewing" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var updated = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Interviewing", updated.RootElement.GetProperty("status").GetString());
        Assert.Equal("original notes", updated.RootElement.GetProperty("notes").GetString());
        Assert.Equal("Senior Engineer",
            updated.RootElement.GetProperty("posting").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Patch_ForAnUnknownId_Returns404()
    {
        var response = await Client.PatchAsJsonAsync(
            $"/applications/{Guid.NewGuid()}", new { status = "Offer" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Patch_RenamingTheCompany_ReusesAnExistingCompanyRowRatherThanDuplicating()
    {
        var id = await Client.CreateApplicationAsync("Seek", "Engineer", Ct);
        await Client.CreateApplicationAsync("REA Group", "Engineer", Ct);

        (await Client.PatchAsJsonAsync($"/applications/{id}", new { company = "REA Group" }, Ct))
            .EnsureSuccessStatusCode();

        // Find-or-create applies on the update path too, not just on create.
        Assert.Equal(2, await WithDbAsync(db => db.Companies.CountAsync(Ct)));
        Assert.Equal(1, await WithDbAsync(db => db.Companies.CountAsync(c => c.Name == "REA Group", Ct)));
    }

    [Fact]
    public async Task Delete_Returns204_AndThen404OnASecondAttempt()
    {
        var id = await Client.CreateApplicationAsync("Telstra", "Engineer", Ct);

        var first = await Client.DeleteAsync($"/applications/{id}", Ct);
        var second = await Client.DeleteAsync($"/applications/{id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task GetAll_ReturnsEveryApplication_WithTheFullEagerLoadedGraph()
    {
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        (await Client.AddSkillAsync(id, "C#", Ct)).EnsureSuccessStatusCode();
        await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);

        var response = await Client.GetAsync("/applications", Ct);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        // Deliberately asserts the count and the graph depth, not the order: GetAllAsync
        // sorts by DateApplied, which is a DateOnly, so same-day rows tie and their
        // relative order is undefined. An ordering assertion here would be flaky.
        Assert.Equal(2, body.RootElement.GetArrayLength());

        var withSkill = body.RootElement.EnumerateArray()
            .Single(a => a.GetProperty("id").GetGuid() == id);
        Assert.Equal("Canva",
            withSkill.GetProperty("posting").GetProperty("company").GetProperty("name").GetString());
        Assert.Equal(1,
            withSkill.GetProperty("posting").GetProperty("postingSkills").GetArrayLength());
    }
}
