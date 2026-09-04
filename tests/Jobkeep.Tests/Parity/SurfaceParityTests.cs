using System.Net;
using System.Net.Http.Json;
using Jobkeep.Modules.Ai.Domain;
using Jobkeep.Modules.Applications;
using Jobkeep.Modules.Applications.Domain;
using Jobkeep.Modules.Match.Domain;
using Jobkeep.Modules.Skills.Domain;
using Jobkeep.SharedKernel;
using Jobkeep.Tests.Domain;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Parity;

/// <summary>
/// REST and GraphQL sit on the same slice handlers. Decision 10 in architecture.md says a
/// handler returns a SliceResult and each surface translates it at its own edge — REST to
/// 404/400, GraphQL to NOT_FOUND/INVALID_INPUT.
///
/// That is a claim about behaviour, and nothing in the compiler enforces it. These tests
/// are what stop it from quietly becoming false.
///
/// They also used to record the two places where it was <em>already</em> false — the A4
/// tests at the bottom, which asserted that GraphQL accepted a blank title REST rejected
/// and that PATCH validated nothing at all. Phase 2.3 moved create and update into slices,
/// so those two failed, as they were written to. They are flipped now, and the comment on
/// each says what it used to assert: the point of writing a finding as an executable test
/// is that fixing the finding is what breaks it.
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
        var names = rest.RootElement.GetProperty("posting").GetProperty("skills")
            .EnumerateArray()
            .Select(ps => ps.GetProperty("skillName").GetString());
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

    [Fact]
    public async Task TheSameFilter_ReturnsTheSameResultOverBothSurfaces()
    {
        // Phase 2.3's version of the parity claim. Filtering is a business rule, so it
        // lives in ListApplicationsHandler where both surfaces reach it — the point being
        // that neither surface can offer a filter, or a default, the other does not.
        var withCSharp = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);
        (await Client.AddSkillAsync(withCSharp, "C#", Ct)).EnsureSuccessStatusCode();

        var restResponse = await Client.GetAsync("/applications?skill=C%23", Ct);
        using var rest = System.Text.Json.JsonDocument.Parse(
            await restResponse.Content.ReadAsStringAsync(Ct));

        var graphql = await GraphQL.QueryAsync(
            """
            { applications(query: { skill: "C#" }) { totalCount items { id company } } }
            """);

        var page = graphql.Data!.Value.GetProperty("applications");
        Assert.Equal(rest.RootElement.GetProperty("totalCount").GetInt32(),
            page.GetProperty("totalCount").GetInt32());
        Assert.Equal(withCSharp,
            page.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task InvalidPaging_Is400OverRest_AndINVALID_INPUTOverGraphQL()
    {
        var rest = await Client.GetAsync("/applications?pageSize=500", Ct);
        var graphql = await GraphQL.QueryAsync(
            "{ applications(query: { pageSize: 500 }) { totalCount } }");

        Assert.Equal(HttpStatusCode.BadRequest, rest.StatusCode);
        Assert.Equal("INVALID_INPUT", graphql.FirstErrorCode);
        Assert.Equal("pageSize must be between 1 and 100.", graphql.FirstErrorMessage);
    }

    // ------------------------------------------------------------------
    // Was A4 — "validation is ad hoc and surface-specific". Fixed in Phase 2.3.
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateApplication_RejectsABlankTitleOverBothSurfaces()
    {
        // Until Phase 2.3 this test was named A4_RejectsBlankTitleOverRest_ButAcceptsIt-
        // OverGraphQL, and it asserted exactly that: the GraphQL mutation called the
        // repository directly and never ran the null check ApplicationEndpoints.Create
        // performed, so the same input succeeded here and 400'd there. One rule, two
        // implementations — the thing vertical slices exist to stop.
        //
        // The rule now lives in CreateApplicationHandler, which both surfaces call.
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
        Assert.Equal("INVALID_INPUT", graphql.FirstErrorCode);
        Assert.Equal("Company and Title are required.", graphql.FirstErrorMessage);

        // Neither surface wrote anything. The old assertion here was the opposite —
        // that GraphQL had left one posting behind.
        Assert.Equal(0, await WithDbAsync(db => db.JobPostings.CountAsync(Ct)));
    }

    [Fact]
    public async Task Patch_EnforcesTheSameTitleRuleThatCreateDoes()
    {
        // Was A4_Patch_HasNoValidation_SoItCanBlankATitleThatCreateWouldHaveRejected.
        // ApplicationEndpoints.Update applied every non-null field with no checks, and an
        // empty string is not null, so it wrote through — leaving a posting in a state
        // POST /applications would have refused to create.
        var id = await Client.CreateApplicationAsync("Atlassian", "Backend Engineer", Ct);

        var response = await Client.PatchAsJsonAsync($"/applications/{id}", new { title = "" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var unchanged = await Client.GetApplicationAsync(id, Ct);
        Assert.Equal("Backend Engineer",
            unchanged.RootElement.GetProperty("posting").GetProperty("title").GetString());
    }

    // ------------------------------------------------------------------
    // Phase 2.5 — the status lifecycle. The table itself is unit-tested in
    // Domain/ApplicationStatusTransitionTests; what needs a real app and a real
    // database is everything around it.
    // ------------------------------------------------------------------

    [Fact]
    public async Task AnIllegalTransition_Is400OverRest_AndINVALID_INPUTOverGraphQL()
    {
        // Offer -> Applied. Both surfaces call UpdateApplicationHandler, so the refusal
        // and its wording come from one place — the same argument as the A4 tests above,
        // now applied to a domain rule rather than to input validation.
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        (await Client.PatchAsJsonAsync($"/applications/{id}", new { status = "Offer" }, Ct))
            .EnsureSuccessStatusCode();

        var rest = await Client.PatchAsJsonAsync(
            $"/applications/{id}", new { status = "Applied" }, Ct);
        var graphql = await GraphQL.QueryAsync(
            """
            mutation ($id: UUID!) {
              updateApplication(id: $id, input: { status: APPLIED }) { id status }
            }
            """,
            new { id });

        Assert.Equal(HttpStatusCode.BadRequest, rest.StatusCode);
        Assert.Equal("INVALID_INPUT", graphql.FirstErrorCode);
        Assert.Equal("Cannot move from Offer to Applied.", graphql.FirstErrorMessage);
        Assert.Contains("Cannot move from Offer to Applied.", await rest.Content.ReadAsStringAsync(Ct));

        using var unchanged = await Client.GetApplicationAsync(id, Ct);
        Assert.Equal("Offer", unchanged.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ARefusedTransition_LeavesTheRestOfThePatchUnapplied()
    {
        // The check runs before any field is assigned, so a PATCH carrying both a legal
        // Notes change and an illegal status change writes neither. The alternative —
        // save what is valid, refuse the rest — would make a 400 mean "some of your
        // request landed", which no caller can act on.
        var id = await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);
        (await Client.PatchAsJsonAsync($"/applications/{id}", new { status = "Offer" }, Ct))
            .EnsureSuccessStatusCode();

        var response = await Client.PatchAsJsonAsync(
            $"/applications/{id}", new { status = "Interviewing", notes = "recruiter called" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var unchanged = await Client.GetApplicationAsync(id, Ct);
        Assert.Equal("Offer", unchanged.RootElement.GetProperty("status").GetString());
        Assert.Null(unchanged.RootElement.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task APatchThatDoesNotMentionStatus_IsNeverATransition()
    {
        // The case that would make the feature feel broken: an application parked in a
        // closed status must still accept an edit to any other field. Unit-tested as
        // IsAllowed(x, x), pinned here through the real PATCH path because the handler
        // is what decides whether "omitted" and "unchanged" are the same thing.
        var id = await Client.CreateApplicationAsync("Seek", "Engineer", Ct);
        (await Client.PatchAsJsonAsync($"/applications/{id}", new { status = "Rejected" }, Ct))
            .EnsureSuccessStatusCode();

        var response = await Client.PatchAsJsonAsync(
            $"/applications/{id}", new { notes = "worth trying again in six months" }, Ct);

        response.EnsureSuccessStatusCode();

        using var updated = await Client.GetApplicationAsync(id, Ct);
        Assert.Equal("Rejected", updated.RootElement.GetProperty("status").GetString());
        Assert.Equal("worth trying again in six months",
            updated.RootElement.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task AClosedApplicationCanBeReopened_ButNotStraightToAnOffer()
    {
        // The decision that came from checking Huntr and Teal: neither treats a closed
        // stage as a one-way door, so neither does this. The half that is still enforced
        // is the half worth having — an offer cannot be conjured out of a rejection.
        var id = await Client.CreateApplicationAsync("Xero", "Senior Engineer", Ct);
        (await Client.PatchAsJsonAsync($"/applications/{id}", new { status = "Rejected" }, Ct))
            .EnsureSuccessStatusCode();

        var toOffer = await Client.PatchAsJsonAsync(
            $"/applications/{id}", new { status = "Offer" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, toOffer.StatusCode);

        var reopened = await Client.PatchAsJsonAsync(
            $"/applications/{id}", new { status = "Interviewing" }, Ct);
        reopened.EnsureSuccessStatusCode();

        using var updated = await Client.GetApplicationAsync(id, Ct);
        Assert.Equal("Interviewing", updated.RootElement.GetProperty("status").GetString());
    }

    // ------------------------------------------------------------------
    // Was A7 — EF entities reachable through the published GraphQL schema.
    // ------------------------------------------------------------------

    [Fact]
    public async Task NoEfEntityIsReachableFromTheGraphQLSchema()
    {
        // A7 was found by reading the emitted SDL, not by reasoning about the model
        // classes — the [JsonIgnore] attributes that hide back-references from REST mean
        // nothing to HotChocolate, which honours [GraphQLIgnore]. So publishing
        // JobApplication published Company.postings, JobPosting.applications and
        // Skill.postingSkills with it, and a client could walk
        //   application -> posting -> company -> postings -> applications -> resumeText
        // to read every résumé in the database.
        //
        // Phase 2.3 put DTOs on every root field. HotChocolate builds the schema from
        // return types, so the entity types are not in it at all — which closes the walk
        // by construction rather than by remembering to annotate each new navigation
        // property. Asserted against the SDL for the same reason it was found there.
        var sdl = await Client.GetStringAsync("/graphql?sdl", Ct);

        foreach (var entity in new[]
                 {
                     "JobApplication", "JobPosting", "Company", "Skill",
                     "PostingSkill", "JobRequirement", "MatchResult", "AiAnalysis",
                 })
        {
            Assert.DoesNotContain($"type {entity} ", sdl);
        }

        // And the replacements are there, so this cannot pass by the schema being empty.
        Assert.Contains("type ApplicationDetail ", sdl);
        Assert.Contains("type ApplicationPage ", sdl);
    }
}
