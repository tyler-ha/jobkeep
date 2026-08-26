using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Rest;

/// <summary>
/// Create, read, update and delete over REST.
///
/// Until Phase 2.3 these routes lived in Endpoints/ApplicationEndpoints.cs and went
/// through IJobApplicationRepository, returning EF entities. They are slices now
/// (Modules/Applications/), returning ApplicationDetail — so several assertions here
/// changed shape in that phase even though the behaviour they check did not.
///
/// Status codes are asserted exactly rather than via IsSuccessStatusCode, because the
/// routes on /applications deliberately differ: 201 here, 200 on the sub-resources.
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

    [Fact]
    public async Task Create_ReturnsTheDetailDto_NotTheEntity()
    {
        // The A2 fix, pinned at the wire. The old response was a serialized
        // JobApplication: it carried postingId next to posting, and posting.company
        // dragged its whole navigation graph behind ReferenceHandler.IgnoreCycles.
        // ApplicationDetail carries neither.
        var response = await Client.PostAsJsonAsync("/applications", new
        {
            company = "Canva",
            title = "Backend Engineer",
        }, Ct);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = body.RootElement;

        Assert.False(root.TryGetProperty("postingId", out _));
        Assert.False(root.TryGetProperty("atsResult", out _));

        var posting = root.GetProperty("posting");
        Assert.False(posting.TryGetProperty("companyId", out _));
        Assert.False(posting.TryGetProperty("applications", out _));
        Assert.False(posting.TryGetProperty("aiAnalysis", out _));
        Assert.False(posting.GetProperty("company").TryGetProperty("postings", out _));

        // Renamed on the way out: the join table is an implementation detail, so the
        // contract says "skills", flattened, rather than "postingSkills" wrapping a
        // nested skill row.
        Assert.Equal(JsonValueKind.Array, posting.GetProperty("skills").ValueKind);
        Assert.Equal(JsonValueKind.Array, posting.GetProperty("requirements").ValueKind);
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
        //
        // The message is unchanged from Phase 1 on purpose. The rule moved out of the
        // endpoint and into CreateApplicationHandler in Phase 2.3 so that GraphQL runs
        // it too; a caller should not be able to tell that from the response.
        Assert.Equal("\"Company and Title are required.\"", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Create_TrimsCompanyAndTitle()
    {
        var response = await Client.PostAsJsonAsync(
            "/applications", new { company = "  Canva  ", title = "  Backend Engineer  " }, Ct);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var posting = body.RootElement.GetProperty("posting");

        Assert.Equal("Backend Engineer", posting.GetProperty("title").GetString());
        Assert.Equal("Canva", posting.GetProperty("company").GetProperty("name").GetString());

        // Matters more than it looks: companies.Name is a unique natural key, so an
        // untrimmed "Canva " would be a second employer that never dedups against the
        // first, and every company-level rollup would split in two.
        Assert.Equal(1, await WithDbAsync(db => db.Companies.CountAsync(Ct)));
    }

    [Fact]
    public async Task GetById_ForAnUnknownId_Returns404WithTheIdInTheMessage()
    {
        var unknown = Guid.NewGuid();

        var response = await Client.GetAsync($"/applications/{unknown}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // The body changed in Phase 2.3. The old endpoint returned a bare
        // Results.NotFound() with no content; the slice returns a SliceResult carrying a
        // message, which ToHttpResult renders as the body. Same status, more to read.
        Assert.Equal($"\"Application {unknown} not found.\"",
            await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task GetById_WithANonGuidId_Returns404BecauseTheRouteConstraintDoesNotMatch()
    {
        // The {id:guid} constraint means a malformed id never reaches the handler.
        var response = await Client.GetAsync("/applications/not-a-guid", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));
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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Patch_WithABlankTitle_Returns400(string title)
    {
        // Phase 2.3 closed this hole. The retired endpoint applied every non-null field
        // with no checks, and an empty string is not null — so PATCH could write a blank
        // title that POST would have rejected, leaving a row the create path calls
        // invalid. See the SurfaceParityTests note on A4.
        var id = await Client.CreateApplicationAsync("Atlassian", "Backend Engineer", Ct);

        var response = await Client.PatchAsJsonAsync($"/applications/{id}", new { title }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("\"Title must not be blank.\"", await response.Content.ReadAsStringAsync(Ct));

        // And nothing was written on the way to the 400.
        using var unchanged = await Client.GetApplicationAsync(id, Ct);
        Assert.Equal("Backend Engineer",
            unchanged.RootElement.GetProperty("posting").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Patch_OmittingAFieldIsNotTheSameAsSendingItBlank()
    {
        // The distinction the handler is built on: `is not null` separates "I am not
        // touching this" from "set this to empty". Collapsing them would make a blank
        // title silently ignored rather than rejected — the same bug, quieter.
        var id = await Client.CreateApplicationAsync("Seek", "Engineer", Ct, notes: "keep me");

        var response = await Client.PatchAsJsonAsync(
            $"/applications/{id}", new { location = "Melbourne" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var updated = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("keep me", updated.RootElement.GetProperty("notes").GetString());
        Assert.Equal("Melbourne",
            updated.RootElement.GetProperty("posting").GetProperty("location").GetString());
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

        // Find-or-create applies on the update path too, not just on create — both now
        // go through the same CompanyLookup.
        Assert.Equal(2, await WithDbAsync(db => db.Companies.CountAsync(Ct)));
        Assert.Equal(1, await WithDbAsync(db => db.Companies.CountAsync(c => c.Name == "REA Group", Ct)));

        // And the rename re-pointed this posting rather than renaming the shared row,
        // which would have moved every other Seek application too.
        Assert.Equal(1, await WithDbAsync(db => db.Companies.CountAsync(c => c.Name == "Seek", Ct)));
    }

    [Fact]
    public async Task Patch_BumpsUpdatedAtUtc()
    {
        var id = await Client.CreateApplicationAsync("Telstra", "Engineer", Ct);
        using var before = await Client.GetApplicationAsync(id, Ct);
        var createdAt = before.RootElement.GetProperty("updatedAtUtc").GetDateTime();

        (await Client.PatchAsJsonAsync($"/applications/{id}", new { notes = "phone screen" }, Ct))
            .EnsureSuccessStatusCode();

        using var after = await Client.GetApplicationAsync(id, Ct);
        Assert.True(after.RootElement.GetProperty("updatedAtUtc").GetDateTime() > createdAt);

        // Worth knowing what this does NOT prove: UpdateApplicationHandler is the only
        // write path in the codebase that maintains this column. Adding a skill or a
        // requirement saves without touching it, so the timestamp is already partly a
        // lie — architecture.md A8, waiting on a SaveChangesInterceptor.
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
}
