using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Parity;

/// <summary>
/// REST and GraphQL sit on the same slice handlers. Decision 10 in architecture.md says a
/// handler returns a SliceResult and each surface translates it at its own edge — REST to
/// 404/400, GraphQL to NOT_FOUND/INVALID_INPUT.
///
/// That is a claim about behaviour, and nothing in the compiler enforces it. These tests
/// are what stop it from quietly becoming false, and they also record the two places
/// where it is *already* false (see the A4 tests at the bottom).
/// </summary>
public sealed class SurfaceParityTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task UnknownApplication_Is404OverRest_AndNOT_FOUNDOverGraphQL()
    {
        var unknown = Guid.NewGuid();

        var rest = await Client.AddSkillAsync(unknown, "C#", Ct);
        var graphql = await GraphQL.QueryAsync(
            """
            mutation ($id: UUID!) {
              addSkillToPosting(applicationId: $id, input: { skillName: "C#", isRequired: true }) {
                skillName
              }
            }
            """,
            new { id = unknown });

        Assert.Equal(HttpStatusCode.NotFound, rest.StatusCode);
        Assert.Equal("NOT_FOUND", graphql.FirstErrorCode);

        // Same handler, so the same message reaches both surfaces.
        Assert.Equal($"Application {unknown} not found.", graphql.FirstErrorMessage);
        Assert.Contains($"Application {unknown} not found.", await rest.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task BlankSkillName_Is400OverRest_AndINVALID_INPUTOverGraphQL()
    {
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        var rest = await Client.PostAsJsonAsync(
            $"/applications/{id}/skills", new { skillName = "", isRequired = false }, Ct);
        var graphql = await GraphQL.QueryAsync(
            """
            mutation ($id: UUID!) {
              addSkillToPosting(applicationId: $id, input: { skillName: "", isRequired: false }) {
                skillName
              }
            }
            """,
            new { id });

        Assert.Equal(HttpStatusCode.BadRequest, rest.StatusCode);
        Assert.Equal("INVALID_INPUT", graphql.FirstErrorCode);
        Assert.Equal("skillName is required.", graphql.FirstErrorMessage);
    }

    [Fact]
    public async Task BlankRequirementText_Is400OverRest_AndINVALID_INPUTOverGraphQL()
    {
        var id = await Client.CreateApplicationAsync("Atlassian", "Backend Engineer", Ct);

        var rest = await Client.PostAsJsonAsync($"/applications/{id}/requirements",
            new { text = "", kind = "Qualification", isMustHave = false }, Ct);
        var graphql = await GraphQL.QueryAsync(
            """
            mutation ($id: UUID!) {
              addRequirementToPosting(
                applicationId: $id
                input: { text: "", kind: QUALIFICATION, isMustHave: false }
              ) { id }
            }
            """,
            new { id });

        Assert.Equal(HttpStatusCode.BadRequest, rest.StatusCode);
        Assert.Equal("INVALID_INPUT", graphql.FirstErrorCode);
    }

    [Fact]
    public async Task AddingASkillOverGraphQL_IsVisibleOverRest_BecauseBothShareOneDataLayer()
    {
        var id = await Client.CreateApplicationAsync("Xero", "Senior Engineer", Ct);

        var graphql = await GraphQL.QueryAsync(
            """
            mutation ($id: UUID!) {
              addSkillToPosting(
                applicationId: $id
                input: { skillName: "Terraform", category: "Infra", isRequired: true }
              ) { skillName category isRequired source }
            }
            """,
            new { id });

        Assert.False(graphql.HasErrors);

        // The Phase 2.1 breaking change, pinned: the mutation takes an input OBJECT and
        // returns PostingSkillResponse rather than the whole JobApplication aggregate.
        var payload = graphql.Data!.Value.GetProperty("addSkillToPosting");
        Assert.Equal("Terraform", payload.GetProperty("skillName").GetString());
        Assert.Equal("PARSED", payload.GetProperty("source").GetString());

        using var rest = await Client.GetApplicationAsync(id, Ct);
        var names = rest.RootElement.GetProperty("posting").GetProperty("postingSkills")
            .EnumerateArray()
            .Select(ps => ps.GetProperty("skill").GetProperty("name").GetString());
        Assert.Contains("Terraform", names);
    }

    [Fact]
    public async Task EnumsAreScreamingCaseOverGraphQL_AndPascalCaseOverRest()
    {
        var id = await Client.CreateApplicationAsync("Seek", "Engineer", Ct);
        (await Client.PatchAsJsonAsync($"/applications/{id}", new { status = "Interviewing" }, Ct))
            .EnsureSuccessStatusCode();

        var graphql = await GraphQL.QueryAsync(
            "query ($id: UUID!) { application(id: $id) { status posting { employmentType } } }",
            new { id });

        var application = graphql.Data!.Value.GetProperty("application");
        Assert.Equal("INTERVIEWING", application.GetProperty("status").GetString());
        Assert.Equal("FULL_TIME", application.GetProperty("posting").GetProperty("employmentType").GetString());

        using var rest = await Client.GetApplicationAsync(id, Ct);
        Assert.Equal("Interviewing", rest.RootElement.GetProperty("status").GetString());
    }

    // ------------------------------------------------------------------
    // Known asymmetries. These assert what the code does TODAY, not what it
    // should do. They are architecture.md A4 — "validation is ad hoc and
    // surface-specific" — expressed as executable findings rather than prose.
    //
    // A skipped test would rot; a test asserting the fixed behaviour would fail
    // and get muted. Asserting the current behaviour means the day Phase 2.2 or
    // 2.4 centralises validation, these fail loudly and get flipped, which is
    // exactly the signal wanted.
    // ------------------------------------------------------------------

    [Fact]
    public async Task A4_CreateApplication_RejectsBlankTitleOverRest_ButAcceptsItOverGraphQL()
    {
        var rest = await Client.PostAsJsonAsync(
            "/applications", new { company = "Canva", title = "" }, Ct);

        var graphql = await GraphQL.QueryAsync(
            """
            mutation {
              createApplication(input: { company: "Canva", title: "" }) {
                id posting { title }
              }
            }
            """);

        Assert.Equal(HttpStatusCode.BadRequest, rest.StatusCode);

        // The GraphQL mutation calls the repository directly and never runs the null
        // check that ApplicationEndpoints.Create performs, so the same input succeeds
        // here. One rule, two implementations — the thing vertical slices exist to stop.
        Assert.False(graphql.HasErrors);
        Assert.Equal("", graphql.Data!.Value
            .GetProperty("createApplication").GetProperty("posting").GetProperty("title").GetString());
        Assert.Equal(1, await WithDbAsync(db => db.JobPostings.CountAsync(Ct)));
    }

    [Fact]
    public async Task A4_Patch_HasNoValidation_SoItCanBlankATitleThatCreateWouldHaveRejected()
    {
        // ApplicationEndpoints.Update applies every non-null field with no checks. An
        // empty string is not null, so it writes through — leaving a posting in a state
        // POST /applications would have refused to create.
        var id = await Client.CreateApplicationAsync("Atlassian", "Backend Engineer", Ct);

        var response = await Client.PatchAsJsonAsync($"/applications/{id}", new { title = "" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("", body.RootElement.GetProperty("posting").GetProperty("title").GetString());
    }
}
