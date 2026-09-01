using System.Net;
using System.Text.Json;
using Jobkeep.Models;
using Jobkeep.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Tests.Ats;

/// <summary>
/// Phase 5 — the ATS check, with everything real except the model.
///
/// <para>
/// The centre of gravity here is the skill gap, and it is worth saying why these
/// tests assert on it <em>by name</em> rather than by count. The gap is a set
/// difference over the shared <c>skills</c> table: a posting's "C#" and a resume's
/// "C#" are the same row, joined on SkillId. That was decided in Phase 2, restated
/// in Phase 4.5, and until this slice existed it had never actually been run.
/// A count assertion would pass on a query that matched nothing and a query that
/// matched everything shifted by one; naming the skills is what proves the join.
/// </para>
///
/// <para>
/// Postings are arranged through the real HTTP surface, resumes are seeded
/// directly. That asymmetry is deliberate rather than lazy: arranging a resume
/// through its own surface means uploading a file, faking a model reply,
/// reviewing a draft and confirming it — four requests to produce fixture data,
/// each of which is already covered by the Phase 4.5 import tests. What these
/// tests need from a resume is its rows. What they need from a posting is its
/// rows created the way the app creates them, because the skill row the resume
/// links to has to be the same one the posting made.
/// </para>
/// </summary>
public class AtsTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    /// <summary>
    /// A client against the real app with the model swapped. Same construction as
    /// AnalyzerTests.AppWithModel — last registration wins for a single resolve, so
    /// this replaces the Ollama client without the shared fixture knowing Ats exists.
    /// </summary>
    private (HttpClient Client, FakeChatClient Model) AppWithModel(FakeChatClient fake)
    {
        var client = Fixture.App
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IChatClient>(fake)))
            .CreateClient();
        return (client, fake);
    }

    private (HttpClient Client, FakeChatClient Model) AppWithModel(string json) =>
        AppWithModel(new FakeChatClient(json));

    /// <summary>The model saying "the resume evidences requirement 1, and not 2".</summary>
    private const string EvidencesFirstOnly = """
        { "evidencedRequirementNumbers": [1] }
        """;

    private const string EvidencesNothing = """
        { "evidencedRequirementNumbers": [] }
        """;

    /// <summary>
    /// Seeds a resume whose skills are the <em>existing</em> shared skill rows with
    /// these names, failing loudly if one is missing.
    ///
    /// The lookup is the point. If this created its own skill rows, every gap test
    /// would pass trivially — the resume's "C#" would be a different row from the
    /// posting's "C#" and everything would read as missing. Resolving against the
    /// rows the posting already made is what puts the shared table under test.
    /// </summary>
    private Task<Guid> SeedResumeAsync(
        string label,
        IEnumerable<string> skillNames,
        SourceFormat? format = SourceFormat.Docx,
        string? fullName = "Tyler Ha",
        string? email = "tyler@example.com",
        string? location = "Melbourne",
        string? sourceText = null,
        int experiences = 0)
        => WithDbAsync(async db =>
        {
            var resume = new Resume
            {
                Label = label,
                FullName = fullName,
                Email = email,
                Location = location,
                SourceFormat = format,
                // Long enough not to trip the "implausibly short" rule unless a
                // test asks for it. 1,200 characters is about a third of the real
                // CV this phase was built against.
                SourceText = sourceText ?? new string('x', 1200),
            };

            foreach (var name in skillNames)
            {
                var skill = await db.Skills.SingleOrDefaultAsync(s => s.Name == name, Ct)
                    ?? throw new InvalidOperationException(
                        $"No shared skill row named \"{name}\". Arrange the posting first — "
                      + "seeding a new row here would defeat the join these tests exist to prove.");

                resume.ResumeSkills.Add(new ResumeSkill { SkillId = skill.Id, Source = SkillSource.Parsed });
            }

            for (var i = 0; i < experiences; i++)
                resume.Experiences.Add(new ResumeExperience { Employer = $"Employer {i}", Ordinal = i });

            db.Resumes.Add(resume);
            await db.SaveChangesAsync(Ct);
            return resume.Id;
        });

    private static async Task<JsonDocument> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

    private static string[] Names(JsonElement body, string property) =>
        body.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();

    // -----------------------------------------------------------------------
    // The skill gap
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Check_SortsPostingSkillsIntoMatched_MissingMustHave_AndMissingNiceToHave()
    {
        // The test this phase exists for. Four posting skills, two of them on the
        // resume, and the two that are not split by IsRequired.
        var (client, _) = AppWithModel(EvidencesNothing);
        var id = await client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        await client.AddSkillAsync(id, "C#", Ct, isRequired: true);
        await client.AddSkillAsync(id, "PostgreSQL", Ct, isRequired: true);
        await client.AddSkillAsync(id, "Kubernetes", Ct, isRequired: true);
        await client.AddSkillAsync(id, "Terraform", Ct, isRequired: false);

        var resumeId = await SeedResumeAsync("mine", ["C#", "PostgreSQL"]);

        var response = await client.PostAsync($"/applications/{id}/ats-check?resumeId={resumeId}", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = await BodyAsync(response);
        var root = body.RootElement;

        Assert.Equal(["C#", "PostgreSQL"], Names(root, "matchedSkills"));
        Assert.Equal(["Kubernetes"], Names(root, "missingMustHaveSkills"));
        Assert.Equal(["Terraform"], Names(root, "missingNiceToHaveSkills"));

        // And on the database, not just the response body.
        await WithDbAsync(async db =>
        {
            var stored = await db.AtsResults.SingleAsync(Ct);
            Assert.Equal(resumeId, stored.ResumeId);
            Assert.Equal(["Kubernetes"], stored.MissingMustHaveKeywords);
            Assert.Equal(["Terraform"], stored.MissingNiceToHaveKeywords);
        });
    }

    [Fact]
    public async Task Check_DoesNotConflateAMissingNiceToHaveWithAMissingMustHave()
    {
        // Separate from the test above because this is the distinction the extra
        // column was added for. A posting asking for Rust as a bonus must not
        // produce the same output as one that requires it.
        var (client, _) = AppWithModel(EvidencesNothing);
        var id = await client.CreateApplicationAsync("Atlassian", "Engineer", Ct);
        await client.AddSkillAsync(id, "Rust", Ct, isRequired: false);

        var resumeId = await SeedResumeAsync("mine", []);

        using var body = await BodyAsync(
            await client.PostAsync($"/applications/{id}/ats-check?resumeId={resumeId}", null, Ct));

        Assert.Empty(Names(body.RootElement, "missingMustHaveSkills"));
        Assert.Equal(["Rust"], Names(body.RootElement, "missingNiceToHaveSkills"));
    }

    [Fact]
    public async Task Check_SortsSkillNamesCaseInsensitively()
    {
        // Phase 13.2e. The gap used to be one query and `ORDER BY skills."Name"`,
        // so the ordering was whatever the database collation said. It is now an
        // in-memory sort, which means the comparer is a decision this code makes
        // rather than one it inherits — and a silent switch to the default
        // ordinal comparer is exactly the kind of regression a rename-free
        // refactor slips through.
        //
        // These three names separate the two: ordinal puts every uppercase letter
        // before every lowercase one, so it would answer C#, aws, terraform.
        var (client, _) = AppWithModel(EvidencesNothing);
        var id = await client.CreateApplicationAsync("Canva", "Engineer", Ct);

        await client.AddSkillAsync(id, "terraform", Ct, isRequired: true);
        await client.AddSkillAsync(id, "C#", Ct, isRequired: true);
        await client.AddSkillAsync(id, "aws", Ct, isRequired: true);

        var resumeId = await SeedResumeAsync("mine", []);

        using var body = await BodyAsync(
            await client.PostAsync($"/applications/{id}/ats-check?resumeId={resumeId}", null, Ct));

        Assert.Equal(
            ["aws", "C#", "terraform"],
            Names(body.RootElement, "missingMustHaveSkills"));
    }

    [Fact]
    public async Task Check_UsesTheApplicationsOwnResume_WhenNoResumeIdIsPassed()
    {
        var (client, _) = AppWithModel(EvidencesNothing);

        // Arrange a skill row first, then a resume, then an application linked to it.
        var seed = await client.CreateApplicationAsync("Seed", "Seed", Ct);
        await client.AddSkillAsync(seed, "C#", Ct, isRequired: true);
        var resumeId = await SeedResumeAsync("linked", ["C#"]);

        var id = await client.CreateApplicationAsync("Canva", "Engineer", Ct, resumeId: resumeId);
        await client.AddSkillAsync(id, "C#", Ct, isRequired: true);
        await client.AddSkillAsync(id, "Go", Ct, isRequired: true);

        using var body = await BodyAsync(
            await client.PostAsync($"/applications/{id}/ats-check", null, Ct));

        Assert.Equal(resumeId, body.RootElement.GetProperty("resumeId").GetGuid());
        Assert.Equal("linked", body.RootElement.GetProperty("resumeLabel").GetString());
        Assert.Equal(["C#"], Names(body.RootElement, "matchedSkills"));
        Assert.Equal(["Go"], Names(body.RootElement, "missingMustHaveSkills"));
    }

    // -----------------------------------------------------------------------
    // The model half, and what happens without it
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Check_ReportsRequirementsTheModelDidNotEvidence()
    {
        var (client, model) = AppWithModel(EvidencesFirstOnly);
        var id = await client.CreateApplicationAsync("Canva", "Engineer", Ct);
        // Added nice-to-have first, deliberately: the handler numbers must-haves
        // first, so the order they go in is not the order the model sees. If the
        // numbering ever stops being deterministic, the model's "[1]" starts
        // pointing at a different requirement and this test says so.
        await client.AddRequirementAsync(id, "AWS certification", Ct, isMustHave: false);
        await client.AddRequirementAsync(id, "Five years of C#", Ct, isMustHave: true);

        var resumeId = await SeedResumeAsync("mine", []);

        using var body = await BodyAsync(
            await client.PostAsync($"/applications/{id}/ats-check?resumeId={resumeId}", null, Ct));

        // The requirements really were numbered into the prompt, must-have first —
        // without that the model's answer of "[1]" would be meaningless and the
        // mapping back to a requirement untestable.
        Assert.Contains("1. Five years of C#", model.LastPrompt);
        Assert.Contains("2. AWS certification", model.LastPrompt);

        // So requirement 1 was evidenced and 2 was not. Anything the model does not
        // name is reported, which is the direction this stage errs in on purpose.
        Assert.Equal(["AWS certification"], Names(body.RootElement, "unmetRequirements"));
    }

    [Fact]
    public async Task Check_StillReturnsTheSkillGap_WhenTheModelIsUnreachable()
    {
        // The most valuable test in this file. Three of the four stages need no
        // model, so an outage must degrade rather than fail — otherwise the whole
        // feature is as available as Ollama is, for no reason.
        var (client, _) = AppWithModel(FakeChatClient.Unreachable());
        var id = await client.CreateApplicationAsync("Canva", "Engineer", Ct);
        await client.AddSkillAsync(id, "Kubernetes", Ct, isRequired: true);
        await client.AddRequirementAsync(id, "Five years of C#", Ct, isMustHave: true);

        var resumeId = await SeedResumeAsync("mine", []);

        var response = await client.PostAsync($"/applications/{id}/ats-check?resumeId={resumeId}", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = await BodyAsync(response);
        var root = body.RootElement;

        Assert.Equal(["Kubernetes"], Names(root, "missingMustHaveSkills"));
        Assert.Empty(Names(root, "unmetRequirements"));
        Assert.Contains("did not respond", root.GetProperty("warning").GetString());

        // The warning is persisted, not just returned. Without the column, a later
        // read of this row would report an empty UnmetRequirements as "you meet
        // every written requirement" — the same failure DocumentImport.Warning
        // exists to prevent.
        await WithDbAsync(async db =>
            Assert.Contains("did not respond", (await db.AtsResults.SingleAsync(Ct)).Warning));
    }

    [Fact]
    public async Task Check_DoesNotCallTheModel_WhenThePostingHasNoRequirements()
    {
        // Nothing to assess is not a reason to spend seconds of inference. Same
        // guard-before-the-call shape as the analyzer's empty-description check.
        var (client, model) = AppWithModel(EvidencesNothing);
        var id = await client.CreateApplicationAsync("Canva", "Engineer", Ct);
        await client.AddSkillAsync(id, "C#", Ct, isRequired: true);

        var resumeId = await SeedResumeAsync("mine", ["C#"]);

        var response = await client.PostAsync($"/applications/{id}/ats-check?resumeId={resumeId}", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(model.LastPrompt);
    }

    // -----------------------------------------------------------------------
    // Formatting rules — each one a finding from the real-CV test on 2026-08-28
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Check_WarnsAboutPdfLayout_ForAPdfResumeAndNotADocxOne()
    {
        var (client, _) = AppWithModel(EvidencesNothing);
        var id = await client.CreateApplicationAsync("Canva", "Engineer", Ct);

        var pdf = await SeedResumeAsync("from-pdf", [], format: SourceFormat.Pdf);
        var docx = await SeedResumeAsync("from-docx", [], format: SourceFormat.Docx);

        using var fromPdf = await BodyAsync(
            await client.PostAsync($"/applications/{id}/ats-check?resumeId={pdf}", null, Ct));
        using var fromDocx = await BodyAsync(
            await client.PostAsync($"/applications/{id}/ats-check?resumeId={docx}", null, Ct));

        Assert.Contains(Names(fromPdf.RootElement, "formattingRiskNotes"),
            n => n.Contains("imported from a PDF"));
        Assert.DoesNotContain(Names(fromDocx.RootElement, "formattingRiskNotes"),
            n => n.Contains("imported from a PDF"));
    }

    [Fact]
    public async Task Check_WarnsWhenTheImportCouldNotFindTheNameOrContactDetails()
    {
        // Exactly the three fields the designed PDF lost in the real-CV test, which
        // is why the rule catches the failure that happened rather than one that
        // might.
        var (client, _) = AppWithModel(EvidencesNothing);
        var id = await client.CreateApplicationAsync("Canva", "Engineer", Ct);

        var resumeId = await SeedResumeAsync(
            "nameless", [], fullName: null, email: null, location: null);

        using var body = await BodyAsync(
            await client.PostAsync($"/applications/{id}/ats-check?resumeId={resumeId}", null, Ct));

        var note = Assert.Single(Names(body.RootElement, "formattingRiskNotes"),
            n => n.Contains("could not find your"));
        Assert.Contains("name", note);
        Assert.Contains("email address", note);
        Assert.Contains("location", note);
    }

    [Fact]
    public async Task Check_WarnsWhenTheExtractedTextIsTooShortForTheRolesListed()
    {
        var (client, _) = AppWithModel(EvidencesNothing);
        var id = await client.CreateApplicationAsync("Canva", "Engineer", Ct);

        // Three roles and 300 characters. The real CV managed roughly 1,090
        // characters per role, so this is lossy extraction, not brevity.
        var lossy = await SeedResumeAsync(
            "lossy", [], sourceText: new string('x', 300), experiences: 3);
        var fine = await SeedResumeAsync(
            "fine", [], sourceText: new string('x', 3262), experiences: 3);

        using var short_ = await BodyAsync(
            await client.PostAsync($"/applications/{id}/ats-check?resumeId={lossy}", null, Ct));
        using var full = await BodyAsync(
            await client.PostAsync($"/applications/{id}/ats-check?resumeId={fine}", null, Ct));

        Assert.Contains(Names(short_.RootElement, "formattingRiskNotes"),
            n => n.Contains("characters of text were extracted"));
        Assert.DoesNotContain(Names(full.RootElement, "formattingRiskNotes"),
            n => n.Contains("characters of text were extracted"));
    }

    // -----------------------------------------------------------------------
    // Storage shape and the read path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Check_Twice_UpdatesTheSameRowRatherThanInsertingASecond()
    {
        // ats_results is 1:1 with the application and the FK is unique, so a second
        // insert would throw rather than duplicate. Re-checking after importing a
        // better resume is a normal thing to do, so it has to be an update path.
        var (client, _) = AppWithModel(EvidencesNothing);
        var id = await client.CreateApplicationAsync("Canva", "Engineer", Ct);
        await client.AddSkillAsync(id, "C#", Ct, isRequired: true);

        var first = await SeedResumeAsync("first", []);
        var second = await SeedResumeAsync("second", ["C#"]);

        await client.PostAsync($"/applications/{id}/ats-check?resumeId={first}", null, Ct);
        var response = await client.PostAsync($"/applications/{id}/ats-check?resumeId={second}", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await WithDbAsync(async db =>
        {
            var stored = await db.AtsResults.SingleAsync(Ct);
            // Latest wins, and ResumeId is what says which resume the survivor judged.
            Assert.Equal(second, stored.ResumeId);
            Assert.Equal(["C#"], stored.MatchedKeywords);
            Assert.Empty(stored.MissingMustHaveKeywords);
        });
    }

    [Fact]
    public async Task Get_ReturnsTheStoredResult_WithoutCallingTheModel()
    {
        var (writer, _) = AppWithModel(EvidencesFirstOnly);
        var id = await writer.CreateApplicationAsync("Canva", "Engineer", Ct);
        await writer.AddSkillAsync(id, "Kubernetes", Ct, isRequired: true);
        // Must-have first is the order the handler numbers them in, so the model's
        // "[1]" means "Five years of C#" — see the prompt assertions above.
        await writer.AddRequirementAsync(id, "Five years of C#", Ct, isMustHave: true);
        await writer.AddRequirementAsync(id, "AWS certification", Ct, isMustHave: false);

        var resumeId = await SeedResumeAsync("mine", []);
        await writer.PostAsync($"/applications/{id}/ats-check?resumeId={resumeId}", null, Ct);

        // A fresh app whose model would throw if touched. This is the assertion —
        // reading a stored result must not depend on Ollama being up.
        var (reader, model) = AppWithModel(FakeChatClient.Unreachable());
        var response = await reader.GetAsync($"/applications/{id}/ats-check", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(model.LastPrompt);

        using var body = await BodyAsync(response);
        Assert.Equal(["Kubernetes"], Names(body.RootElement, "missingMustHaveSkills"));
        Assert.Equal(["AWS certification"], Names(body.RootElement, "unmetRequirements"));
        Assert.Equal("mine", body.RootElement.GetProperty("resumeLabel").GetString());
    }

    [Fact]
    public async Task Get_ReturnsNotFound_WhenNoCheckHasBeenRun()
    {
        var id = await Client.CreateApplicationAsync("Canva", "Engineer", Ct);
        var response = await Client.GetAsync($"/applications/{id}/ats-check", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -----------------------------------------------------------------------
    // The failure cases
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Check_ReturnsNotFound_ForAnApplicationThatDoesNotExist()
    {
        var (client, _) = AppWithModel(EvidencesNothing);
        var response = await client.PostAsync($"/applications/{Guid.NewGuid()}/ats-check", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Check_ReturnsBadRequest_WhenNoResumeIsLinkedAndNoneIsPassed()
    {
        // A sentence, not a 500. The application is fine and the request is fine;
        // what is missing is a resume to compare against, and the caller can fix it.
        var (client, model) = AppWithModel(EvidencesNothing);
        var id = await client.CreateApplicationAsync("Canva", "Engineer", Ct);

        var response = await client.PostAsync($"/applications/{id}/ats-check", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not linked to a resume", await response.Content.ReadAsStringAsync(Ct));
        Assert.Null(model.LastPrompt);
    }

    [Fact]
    public async Task Check_ReturnsBadRequest_ForAResumeIdThatDoesNotExist()
    {
        // Invalid rather than NotFound: the row named in the route exists, so what
        // is wrong is the id the caller supplied. Mirrors the check the Phase 4.5
        // review added to CreateApplication.
        var (client, _) = AppWithModel(EvidencesNothing);
        var id = await client.CreateApplicationAsync("Canva", "Engineer", Ct);
        var unknown = Guid.NewGuid();

        var response = await client.PostAsync($"/applications/{id}/ats-check?resumeId={unknown}", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"Resume {unknown} not found.", await response.Content.ReadAsStringAsync(Ct));
    }

    // -----------------------------------------------------------------------
    // Surface parity
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CheckAts_ReturnsTheSameBuckets_OverRestAndGraphQL()
    {
        var (client, _) = AppWithModel(EvidencesNothing);
        var graphql = new GraphQLClient(client);

        var id = await client.CreateApplicationAsync("Canva", "Engineer", Ct);
        await client.AddSkillAsync(id, "C#", Ct, isRequired: true);
        await client.AddSkillAsync(id, "Kubernetes", Ct, isRequired: true);
        await client.AddSkillAsync(id, "Terraform", Ct, isRequired: false);

        var resumeId = await SeedResumeAsync("mine", ["C#"]);

        using var rest = await BodyAsync(
            await client.PostAsync($"/applications/{id}/ats-check?resumeId={resumeId}", null, Ct));

        var gql = await graphql.QueryAsync(
            """
            mutation ($id: UUID!, $resumeId: UUID) {
              checkAts(applicationId: $id, resumeId: $resumeId) {
                matchedSkills
                missingMustHaveSkills
                missingNiceToHaveSkills
              }
            }
            """,
            new { id, resumeId });

        Assert.False(gql.HasErrors);
        var mutation = gql.Data!.Value.GetProperty("checkAts");

        Assert.Equal(Names(rest.RootElement, "matchedSkills"), Names(mutation, "matchedSkills"));
        Assert.Equal(Names(rest.RootElement, "missingMustHaveSkills"), Names(mutation, "missingMustHaveSkills"));
        Assert.Equal(Names(rest.RootElement, "missingNiceToHaveSkills"), Names(mutation, "missingNiceToHaveSkills"));

        Assert.Equal(["C#"], Names(mutation, "matchedSkills"));
        Assert.Equal(["Kubernetes"], Names(mutation, "missingMustHaveSkills"));
        Assert.Equal(["Terraform"], Names(mutation, "missingNiceToHaveSkills"));
    }

    [Fact]
    public async Task UnknownApplication_Is404OverRest_AndNOT_FOUNDOverGraphQL()
    {
        var (client, _) = AppWithModel(EvidencesNothing);
        var graphql = new GraphQLClient(client);
        var unknown = Guid.NewGuid();

        var rest = await client.GetAsync($"/applications/{unknown}/ats-check", Ct);
        var gql = await graphql.QueryAsync(
            """
            query ($id: UUID!) { atsResult(applicationId: $id) { checkedAtUtc } }
            """,
            new { id = unknown });

        Assert.Equal(HttpStatusCode.NotFound, rest.StatusCode);
        Assert.Equal("NOT_FOUND", gql.FirstErrorCode);
    }
}
