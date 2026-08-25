using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Rest;

/// <summary>
/// The Phase 2.1 slice routes: skills and requirements as sub-resources of an
/// application. These are the first routes written in the vertical-slice shape, and
/// several of their behaviours are deliberate choices recorded in the phase doc rather
/// than accidents — which is exactly why they need pinning down.
/// </summary>
public sealed class SubResourceTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task AddSkill_Returns200_NotCreated()
    {
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        var response = await Client.AddSkillAsync(id, "C#", Ct, category: "Language", isRequired: true);

        // 200, not 201: the slice returns a PostingSkillResponse through the default
        // ToHttpResult, which has no Created branch. Worth pinning because POST
        // /applications alongside it does return 201.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("C#", body.RootElement.GetProperty("skillName").GetString());
        Assert.Equal("Language", body.RootElement.GetProperty("category").GetString());
        Assert.True(body.RootElement.GetProperty("isRequired").GetBoolean());
        Assert.Equal("Parsed", body.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task AddSkill_Twice_IsIdempotentAndDoesNotOverwriteTheExistingLink()
    {
        // Phase 2.1's stated interview point: the composite primary key already says
        // "at most once per posting", so a repeat add is a no-op returning 200 rather
        // than a 400 conflict. The second half matters more — the response reflects the
        // EXISTING row, so isRequired:false does not downgrade the earlier true.
        var id = await Client.CreateApplicationAsync("Atlassian", "Backend Engineer", Ct);
        (await Client.AddSkillAsync(id, "C#", Ct, isRequired: true)).EnsureSuccessStatusCode();

        var second = await Client.AddSkillAsync(id, "C#", Ct, isRequired: false);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct));
        Assert.True(body.RootElement.GetProperty("isRequired").GetBoolean());
        Assert.Equal(1, await WithDbAsync(db => db.PostingSkills.CountAsync(Ct)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddSkill_WithABlankName_Returns400(string skillName)
    {
        var id = await Client.CreateApplicationAsync("Xero", "Senior Engineer", Ct);

        var response = await Client.PostAsJsonAsync(
            $"/applications/{id}/skills", new { skillName, isRequired = false }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("\"skillName is required.\"", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task AddSkill_ToAnUnknownApplication_Returns404WithTheIdInTheMessage()
    {
        var unknown = Guid.NewGuid();

        var response = await Client.AddSkillAsync(unknown, "C#", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal($"\"Application {unknown} not found.\"",
            await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task RemoveSkill_Returns204_AndUnlinkingTwiceReturns404()
    {
        var id = await Client.CreateApplicationAsync("Seek", "Engineer", Ct);
        (await Client.AddSkillAsync(id, "Postgres", Ct)).EnsureSuccessStatusCode();

        var first = await Client.DeleteAsync($"/applications/{id}/skills/Postgres", Ct);
        var second = await Client.DeleteAsync($"/applications/{id}/skills/Postgres", Ct);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal($"\"Skill 'Postgres' is not linked to application {id}.\"",
            await second.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("C#", "C%23")]
    [InlineData("C++", "C%2B%2B")]
    [InlineData(".NET", ".NET")]
    public async Task SkillNamesWithAwkwardCharacters_RoundTripThroughThePath(
        string skillName, string encoded)
    {
        // The skill name is a path segment, so the client has to encode it. These are the
        // three shapes a real skill list actually contains, and '#' in particular would be
        // silently truncated to a fragment if sent raw.
        var id = await Client.CreateApplicationAsync("REA Group", "Engineer", Ct);
        (await Client.AddSkillAsync(id, skillName, Ct)).EnsureSuccessStatusCode();

        var response = await Client.DeleteAsync($"/applications/{id}/skills/{encoded}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await WithDbAsync(db => db.PostingSkills.CountAsync(Ct)));
    }

    [Fact]
    public async Task AddRequirement_Returns200_AndPersistsKindAndMustHave()
    {
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        var response = await Client.AddRequirementAsync(
            id, "5+ years .NET", Ct, kind: "Qualification", isMustHave: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("5+ years .NET", body.RootElement.GetProperty("text").GetString());
        Assert.Equal("Qualification", body.RootElement.GetProperty("kind").GetString());
        Assert.True(body.RootElement.GetProperty("isMustHave").GetBoolean());
        Assert.NotEqual(Guid.Empty, body.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AddRequirement_DoesNotDeduplicate_SoTheSameTextTwiceIsTwoRows()
    {
        // Documented as deliberate in AddRequirementToPosting.cs: unlike skills, there is
        // no natural key on requirement text, so identical text creates a second row.
        var id = await Client.CreateApplicationAsync("Atlassian", "Backend Engineer", Ct);

        (await Client.AddRequirementAsync(id, "5+ years .NET", Ct)).EnsureSuccessStatusCode();
        (await Client.AddRequirementAsync(id, "5+ years .NET", Ct)).EnsureSuccessStatusCode();

        Assert.Equal(2, await WithDbAsync(db => db.JobRequirements.CountAsync(Ct)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddRequirement_WithBlankText_Returns400(string text)
    {
        var id = await Client.CreateApplicationAsync("Xero", "Senior Engineer", Ct);

        var response = await Client.PostAsJsonAsync($"/applications/{id}/requirements",
            new { text, kind = "Qualification", isMustHave = false }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("\"text is required.\"", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task RemoveRequirement_Returns204()
    {
        var id = await Client.CreateApplicationAsync("Seek", "Engineer", Ct);
        var created = await Client.AddRequirementAsync(id, "5+ years .NET", Ct);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        var requirementId = body.RootElement.GetProperty("id").GetGuid();

        var response = await Client.DeleteAsync(
            $"/applications/{id}/requirements/{requirementId}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await WithDbAsync(db => db.JobRequirements.CountAsync(Ct)));
    }

    [Fact]
    public async Task RemoveRequirement_ThroughTheWrongApplication_Returns404AndDeletesNothing()
    {
        // The horizontal-access guard from Phase 2.1: RemoveRequirement matches on the
        // requirement id AND the addressed application's posting id, so naming someone
        // else's parent cannot delete a requirement you did not address. With no auth in
        // the project yet, this scoping rule is the only thing enforcing that boundary.
        var mine = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        var theirs = await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);

        var created = await Client.AddRequirementAsync(theirs, "Kubernetes in production", Ct);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        var requirementId = body.RootElement.GetProperty("id").GetGuid();

        var response = await Client.DeleteAsync(
            $"/applications/{mine}/requirements/{requirementId}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal($"\"Requirement {requirementId} not found on application {mine}.\"",
            await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, await WithDbAsync(db => db.JobRequirements.CountAsync(Ct)));
    }
}
