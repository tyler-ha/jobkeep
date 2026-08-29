using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jobkeep.Models;
using Jobkeep.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Tests.Documents;

/// <summary>
/// POST /resumes/{id}/skills and the addSkillToResume mutation — the resume-side
/// mirror of AddSkillToPosting.
///
/// <para>
/// The assertion that matters most is not "a row appeared". It is that the row
/// this slice writes points at the <em>same</em> shared <c>skills</c> row the
/// posting side already made, because that identity is the only thing that makes
/// the ATS check's gap a join rather than a string comparison. So these tests
/// count rows in <c>skills</c>, not just links in <c>resume_skills</c>.
/// </para>
///
/// <para>
/// The last test is the whole reason the slice exists: it reproduces the near-miss
/// the Phase 5 verification hit against the real CV — a résumé that says PostgreSQL
/// in prose but <c>SQL</c> in its structured skill list, reported as a missing
/// must-have — and proves the correction actually clears it.
/// </para>
/// </summary>
public class ResumeSkillTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string EvidencesNothing = """
        { "evidencedRequirementNumbers": [] }
        """;

    /// <summary>The app with the model swapped, for the ATS re-check at the end.</summary>
    private HttpClient AppWithModel(string json) => Fixture.App
        .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IChatClient>(new FakeChatClient(json))))
        .CreateClient();

    /// <summary>
    /// Seeds a bare résumé with no skills. Unlike AtsTests.SeedResumeAsync this does
    /// not resolve existing skill rows — the point here is that the slice under test
    /// is what puts them on.
    /// </summary>
    private Task<Guid> SeedResumeAsync(string label = "mine", string? sourceText = null)
        => WithDbAsync(async db =>
        {
            var resume = new Resume
            {
                Label = label,
                FullName = "Tyler Ha",
                Email = "tyler@example.com",
                Location = "Melbourne",
                SourceFormat = SourceFormat.Docx,
                SourceText = sourceText ?? new string('x', 1200),
            };
            db.Resumes.Add(resume);
            await db.SaveChangesAsync(Ct);
            return resume.Id;
        });

    private Task<HttpResponseMessage> AddAsync(
        HttpClient client, Guid resumeId, string skillName, string? category = null)
        => client.PostAsJsonAsync($"/resumes/{resumeId}/skills", new { skillName, category }, Ct);

    private static async Task<JsonDocument> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

    // -----------------------------------------------------------------------
    // The shared table
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Add_CreatesTheSharedSkillRowAndTheLink_WhenTheSkillIsNew()
    {
        var resumeId = await SeedResumeAsync();

        var response = await AddAsync(Client, resumeId, "Rust", category: "Language");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = await BodyAsync(response);
        Assert.Equal("Rust", body.RootElement.GetProperty("skillName").GetString());
        Assert.Equal("Language", body.RootElement.GetProperty("category").GetString());
        Assert.Equal("Parsed", body.RootElement.GetProperty("source").GetString());

        await WithDbAsync(async db =>
        {
            var link = await db.ResumeSkills.Include(rs => rs.Skill).SingleAsync(Ct);
            Assert.Equal(resumeId, link.ResumeId);
            Assert.Equal("Rust", link.Skill.Name);
            Assert.Equal(SkillSource.Parsed, link.Source);
        });
    }

    [Fact]
    public async Task Add_ReusesThePostingsSkillRow_RatherThanCreatingASecond()
    {
        // The join this whole design rests on. A posting makes "C#"; the résumé then
        // claims "C#"; there must be exactly ONE row in `skills` afterwards, or the
        // ATS check is comparing two different rows that merely spell the same.
        var applicationId = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        await Client.AddSkillAsync(applicationId, "C#", Ct, isRequired: true);

        var resumeId = await SeedResumeAsync();
        Assert.Equal(HttpStatusCode.OK, (await AddAsync(Client, resumeId, "C#")).StatusCode);

        await WithDbAsync(async db =>
        {
            var skill = await db.Skills.SingleAsync(s => s.Name == "C#", Ct);
            Assert.Equal(1, await db.Skills.CountAsync(s => s.Name == "C#", Ct));

            // Both links point at that one row.
            Assert.True(await db.PostingSkills.AnyAsync(ps => ps.SkillId == skill.Id, Ct));
            Assert.True(await db.ResumeSkills.AnyAsync(rs => rs.SkillId == skill.Id, Ct));
        });
    }

    [Fact]
    public async Task Add_IsANoOp_WhenTheSkillIsAlreadyOnTheResume()
    {
        // The composite PK makes "at most once per résumé" the schema's rule, so a
        // client asking twice is asking for a state that already holds. 200, not 400
        // — the same call AddSkillToPosting makes.
        var resumeId = await SeedResumeAsync();

        Assert.Equal(HttpStatusCode.OK, (await AddAsync(Client, resumeId, "Go")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AddAsync(Client, resumeId, "Go")).StatusCode);

        await WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.ResumeSkills.CountAsync(Ct));
            Assert.Equal(1, await db.Skills.CountAsync(Ct));
        });
    }

    [Fact]
    public async Task Add_PromotesAnAiExtractedLinkToParsed_BecauseAHumanOutranksTheModel()
    {
        // The mirror of what IPostingContract refuses to do in the other direction:
        // the model never restamps a human's row, and a human always restamps the
        // model's. Without this, confirming a skill the parser guessed would look
        // like a no-op and the provenance would stay wrong forever.
        var resumeId = await SeedResumeAsync();

        await WithDbAsync(async db =>
        {
            var skill = new Skill { Name = "Kubernetes" };
            db.Skills.Add(skill);
            db.ResumeSkills.Add(new ResumeSkill
            {
                ResumeId = resumeId,
                SkillId = skill.Id,
                Source = SkillSource.AiExtracted,
            });
            await db.SaveChangesAsync(Ct);
        });

        using var body = await BodyAsync(await AddAsync(Client, resumeId, "Kubernetes"));
        Assert.Equal("Parsed", body.RootElement.GetProperty("source").GetString());

        await WithDbAsync(async db =>
        {
            var link = await db.ResumeSkills.SingleAsync(Ct);
            Assert.Equal(SkillSource.Parsed, link.Source);
        });
    }

    // -----------------------------------------------------------------------
    // Refusals, on both surfaces
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Add_Returns404_ForAResumeThatDoesNotExist()
    {
        var response = await AddAsync(Client, Guid.NewGuid(), "C#");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Add_Returns400_ForABlankSkillName(string skillName)
    {
        var resumeId = await SeedResumeAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await AddAsync(Client, resumeId, skillName)).StatusCode);
    }

    [Fact]
    public async Task Add_Returns400_ForASkillNameOverTheColumnLength()
    {
        // Refused rather than truncated: silently storing the first 100 characters
        // of a paste accident creates a junk row in the table whose entire job is
        // deduplication.
        var resumeId = await SeedResumeAsync();
        var response = await AddAsync(Client, resumeId, new string('x', 101));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await WithDbAsync(async db => Assert.Equal(0, await db.Skills.CountAsync(Ct)));
    }

    [Fact]
    public async Task GraphQL_AddsTheSameSkill_AndReportsTheSameErrorCodes()
    {
        // Parity, because the rule lives in the handler and neither surface decides
        // it. One success and one refusal is enough to prove the adapter is thin.
        var resumeId = await SeedResumeAsync();

        const string Mutation = """
            mutation ($resumeId: UUID!, $input: AddSkillToResumeRequestInput!) {
              addSkillToResume(resumeId: $resumeId, input: $input) { skillName source }
            }
            """;

        var ok = await GraphQL.QueryAsync(Mutation, new
        {
            resumeId,
            input = new { skillName = "Terraform", category = (string?)null },
        });
        Assert.False(ok.HasErrors);
        Assert.Equal("Terraform", ok.Data!.Value.GetProperty("addSkillToResume").GetProperty("skillName").GetString());
        Assert.Equal("PARSED", ok.Data!.Value.GetProperty("addSkillToResume").GetProperty("source").GetString());

        var missing = await GraphQL.QueryAsync(Mutation, new
        {
            resumeId = Guid.NewGuid(),
            input = new { skillName = "Terraform", category = (string?)null },
        });
        Assert.Equal("NOT_FOUND", missing.FirstErrorCode);

        var blank = await GraphQL.QueryAsync(Mutation, new
        {
            resumeId,
            input = new { skillName = "  ", category = (string?)null },
        });
        Assert.Equal("INVALID_INPUT", blank.FirstErrorCode);
    }

    // -----------------------------------------------------------------------
    // The near-miss this slice was written for
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Add_ClearsAMissingMustHave_OnTheNextAtsCheck()
    {
        // Phase 5's verification, run against the real CV and a real Melbourne ad,
        // reported PostgreSQL as a missing must-have because the CV names it in
        // prose while its structured skill list says SQL. The gap is a set
        // difference over skill ROWS, so prose could never close it — this is the
        // correction path, end to end.
        var client = AppWithModel(EvidencesNothing);

        var applicationId = await client.CreateApplicationAsync("REA Group", "Senior Backend Engineer", Ct);
        await client.AddSkillAsync(applicationId, "PostgreSQL", Ct, isRequired: true);

        var resumeId = await SeedResumeAsync("tyler-cv-2025", sourceText:
            "Built and tuned PostgreSQL schemas for a high-volume listings service. " + new string('x', 1200));

        // Before: the near-miss.
        using (var before = await BodyAsync(
            await client.PostAsync($"/applications/{applicationId}/ats-check?resumeId={resumeId}", null, Ct)))
        {
            Assert.Equal(
                ["PostgreSQL"],
                before.RootElement.GetProperty("missingMustHaveSkills")
                    .EnumerateArray().Select(e => e.GetString()!).ToArray());
        }

        // The correction: the user says "yes, I have this".
        Assert.Equal(HttpStatusCode.OK, (await AddAsync(client, resumeId, "PostgreSQL")).StatusCode);

        // After: matched, and nothing missing. Re-checking overwrites the stored
        // row (ats_results is 1:1 with the application), so the latest answer wins.
        using (var after = await BodyAsync(
            await client.PostAsync($"/applications/{applicationId}/ats-check?resumeId={resumeId}", null, Ct)))
        {
            Assert.Equal(
                ["PostgreSQL"],
                after.RootElement.GetProperty("matchedSkills")
                    .EnumerateArray().Select(e => e.GetString()!).ToArray());
            Assert.Empty(after.RootElement.GetProperty("missingMustHaveSkills").EnumerateArray());
        }

        await WithDbAsync(async db =>
        {
            var stored = await db.AtsResults.SingleAsync(Ct);
            Assert.Empty(stored.MissingMustHaveKeywords);
        });
    }
}
