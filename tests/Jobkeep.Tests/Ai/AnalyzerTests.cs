using System.Net;
using System.Text.Json;
using Jobkeep.Contracts.Shared;
using Jobkeep.Contracts.Skills;
using Jobkeep.Modules.Ai.Domain;
using Jobkeep.Modules.Skills.Domain;
using Jobkeep.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Tests.Ai;

/// <summary>
/// Phase 4 — the AI analyzer, with everything real except the model itself
/// (see <see cref="FakeChatClient"/> for why that one boundary is faked).
/// </summary>
public class AnalyzerTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    /// <summary>
    /// A client against the real app with the model swapped for a canned reply.
    ///
    /// WithWebHostBuilder rather than a hook on JobkeepAppFactory: the registration
    /// added here lands after Program.cs's own, and last-registered wins for a
    /// single resolve, so this replaces the Ollama client without the shared test
    /// infrastructure needing to know Phase 4 exists.
    /// </summary>
    private (HttpClient Client, FakeChatClient Model) AppWithModel(string json)
    {
        var fake = new FakeChatClient(json);
        var client = Fixture.App
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IChatClient>(fake)))
            .CreateClient().AsTestUser();
        return (client, fake);
    }

    private const string TwoSkills = """
        {
          "seniority": "Senior",
          "summary": "Builds payment services on .NET. Small team, hybrid in Melbourne.",
          "skills": [
            { "name": "C#", "required": true },
            { "name": "Kubernetes", "required": false }
          ]
        }
        """;

    [Fact]
    public async Task Analyze_StoresTheAnalysisAndTheExtractedSkills()
    {
        var (client, _) = AppWithModel(TwoSkills);
        var id = await client.CreateApplicationAsync(
            "Canva", "Senior Engineer", Ct, description: "We need strong C# and some Kubernetes.");

        var response = await client.PostAsync($"/applications/{id}/analyze", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Senior", body.RootElement.GetProperty("seniority").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("skillsAdded").GetInt32());

        // Assert on the database, not the response body: the response is the
        // handler describing what it believes it did, and the point of a real
        // Postgres in these tests is to check what actually landed.
        await WithDbAsync(async db =>
        {
            var stored = await db.AiAnalyses.SingleAsync(Ct);
            Assert.Equal(SeniorityLevel.Senior, stored.Seniority);
            Assert.Contains("payment services", stored.Summary);
            Assert.Equal("llama3.2:3b", stored.ModelUsed);

            // 13.3b: the link no longer carries a Skill navigation, so the name
            // is resolved separately — the same two-step ISkillCatalog gives the
            // application.
            var names = await SkillNamesAsync(db);
            var links = await db.PostingSkills.ToListAsync(Ct);
            Assert.Equal(2, links.Count);
            Assert.All(links, l => Assert.Equal(SkillSource.AiExtracted, l.Source));
            Assert.True(links.Single(l => names[l.SkillId] == "C#").IsRequired);
            Assert.False(links.Single(l => names[l.SkillId] == "Kubernetes").IsRequired);
        });
    }

    [Fact]
    public async Task Analyze_ReturnsNotFound_ForAnApplicationThatDoesNotExist()
    {
        var (client, _) = AppWithModel(TwoSkills);
        var response = await client.PostAsync($"/applications/{Guid.NewGuid()}/analyze", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Analyze_RefusesAPostingWithNoDescription_WithoutCallingTheModel()
    {
        var (client, model) = AppWithModel(TwoSkills);
        var id = await client.CreateApplicationAsync("Atlassian", "Engineer", Ct, description: null);

        var response = await client.PostAsync($"/applications/{id}/analyze", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // The guard has to come before the call, not after. Inference is the
        // expensive part, and asking a model to summarise an empty string spends
        // seconds to produce a confabulation.
        Assert.Null(model.LastPrompt);
    }

    [Fact]
    public async Task Analyze_Twice_UpdatesTheSameRowRatherThanInsertingASecond()
    {
        var (client, _) = AppWithModel(TwoSkills);
        var id = await client.CreateApplicationAsync(
            "Canva", "Engineer", Ct, description: "C# and Kubernetes.");

        await client.PostAsync($"/applications/{id}/analyze", null, Ct);
        var second = await client.PostAsync($"/applications/{id}/analyze", null, Ct);

        // ai_analyses is 1:1 with the posting and the FK is unique, so a second
        // insert would throw rather than duplicate. Re-analyzing after editing a
        // description is a normal thing to do, so it has to be an update path.
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await WithDbAsync(async db => Assert.Equal(1, await db.AiAnalyses.CountAsync(Ct)));

        // The skills were already linked, so the re-run adds none of them again.
        using var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, body.RootElement.GetProperty("skillsAdded").GetInt32());
    }

    [Fact]
    public async Task Analyze_LeavesAHumanEnteredSkillAlone()
    {
        var (client, _) = AppWithModel(TwoSkills);
        var id = await client.CreateApplicationAsync(
            "Canva", "Engineer", Ct, description: "C# and Kubernetes.");

        // The user typed this one in themselves before running the analyzer.
        (await client.AddSkillAsync(id, "C#", Ct, isRequired: true)).EnsureSuccessStatusCode();

        await client.PostAsync($"/applications/{id}/analyze", null, Ct);

        await WithDbAsync(async db =>
        {
            var names = await SkillNamesAsync(db);
            var links = await db.PostingSkills.ToListAsync(Ct);
            // Provenance is not downgraded by a later extraction: a row the user
            // entered stays Parsed even though the model named the same skill.
            Assert.Equal(SkillSource.Parsed, links.Single(l => names[l.SkillId] == "C#").Source);
            Assert.Equal(SkillSource.AiExtracted, links.Single(l => names[l.SkillId] == "Kubernetes").Source);
        });
    }

    [Fact]
    public async Task Analyze_DegradesAnUnrecognisedSeniorityToUnknown_AndKeepsTheRest()
    {
        // "Mid-Senior" is not a SeniorityLevel. Binding the enum directly would
        // fail the whole parse and throw away the summary and the skills with it;
        // the draft type takes it as a string so only that one field degrades.
        var (client, _) = AppWithModel("""
            { "seniority": "Mid-Senior", "summary": "A backend role.",
              "skills": [ { "name": "Go", "required": true } ] }
            """);
        var id = await client.CreateApplicationAsync("Seek", "Engineer", Ct, description: "Go, mostly.");

        var response = await client.PostAsync($"/applications/{id}/analyze", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WithDbAsync(async db =>
        {
            var stored = await db.AiAnalyses.SingleAsync(Ct);
            Assert.Equal(SeniorityLevel.Unknown, stored.Seniority);
            Assert.Equal("A backend role.", stored.Summary);
            Assert.Equal(1, await db.PostingSkills.CountAsync(Ct));
        });
    }

    [Fact]
    public async Task Analyze_SurvivesTheModelNamingTheSameSkillTwice()
    {
        // Models repeat themselves. posting_skills has a composite primary key, so
        // an undeduped batch is a duplicate-key exception on SaveChanges — a 500
        // on a request that did nothing wrong.
        var (client, _) = AppWithModel("""
            { "seniority": "Junior", "summary": "Graduate role.",
              "skills": [ { "name": "C#", "required": true },
                          { "name": "C#", "required": false },
                          { "name": " C# ", "required": false } ] }
            """);
        var id = await client.CreateApplicationAsync("REA", "Grad", Ct, description: "C#.");

        var response = await client.PostAsync($"/applications/{id}/analyze", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WithDbAsync(async db =>
        {
            var link = await db.PostingSkills.SingleAsync(Ct);
            // First occurrence wins, so an early must-have is not downgraded to a
            // nice-to-have by a later repeat.
            Assert.True(link.IsRequired);
        });
    }

    [Fact]
    public async Task GetAnalysis_IsNotFoundBeforeTheAnalyzerHasRun_AndReadableAfter()
    {
        var (client, _) = AppWithModel(TwoSkills);
        var id = await client.CreateApplicationAsync("Canva", "Engineer", Ct, description: "C#.");

        var before = await client.GetAsync($"/applications/{id}/analysis", Ct);
        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);

        await client.PostAsync($"/applications/{id}/analyze", null, Ct);

        var after = await client.GetAsync($"/applications/{id}/analysis", Ct);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        using var body = JsonDocument.Parse(await after.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Senior", body.RootElement.GetProperty("seniority").GetString());
    }

    [Fact]
    public async Task Analyze_TruncatesAnOversizedDescriptionRatherThanSendingItAll()
    {
        var (client, model) = AppWithModel(TwoSkills);
        var huge = new string('x', 20_000);
        var id = await client.CreateApplicationAsync("Canva", "Engineer", Ct, description: huge);

        var response = await client.PostAsync($"/applications/{id}/analyze", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // MaxDescriptionChars defaults to 12000; the prompt wraps a few hundred
        // characters of instructions around it, so the bound is loose on purpose.
        Assert.NotNull(model.LastPrompt);
        Assert.True(model.LastPrompt!.Length < 13_000,
            $"prompt was {model.LastPrompt.Length} chars; the description should have been truncated");
    }

    [Fact]
    public async Task Analyze_ThroughGraphQL_ProducesTheSameStoredResultAsRest()
    {
        var (client, _) = AppWithModel(TwoSkills);
        var graphql = new GraphQLClient(client);
        var id = await client.CreateApplicationAsync(
            "Canva", "Engineer", Ct, description: "C# and Kubernetes.");

        var result = await graphql.QueryAsync(
            """
            mutation ($id: UUID!) {
              analyzePosting(applicationId: $id) {
                seniority
                skillsAdded
                skills { name isRequired }
              }
            }
            """,
            new { id });

        Assert.False(result.HasErrors, result.FirstErrorMessage);
        var data = result.Data!.Value.GetProperty("analyzePosting");
        // Enums are SCREAMING_CASE over GraphQL and PascalCase over REST — that is
        // the one difference the two surfaces are allowed to have.
        Assert.Equal("SENIOR", data.GetProperty("seniority").GetString());
        Assert.Equal(2, data.GetProperty("skillsAdded").GetInt32());

        await WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.AiAnalyses.CountAsync(Ct));
            Assert.Equal(2, await db.PostingSkills.CountAsync(Ct));
        });
    }
}
