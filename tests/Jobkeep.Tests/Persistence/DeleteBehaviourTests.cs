using System.Net;
using Jobkeep.Models;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Persistence;

/// <summary>
/// The per-relationship delete behaviour configured in AppDbContext.cs.
///
/// docs/architecture.md §4 lists "deliberate delete behaviour" as something the schema
/// already gets right, which is exactly the kind of claim that rots silently — nothing
/// fails a build when a Restrict quietly becomes a Cascade and starts eating rows.
/// </summary>
public sealed class DeleteBehaviourTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task DeletingAnApplication_LeavesThePostingAndCompanyBehind()
    {
        // Restrict on job_applications.PostingId: the ad is a fact about the world and
        // outlives your record of having applied to it.
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        var response = await Client.DeleteAsync($"/applications/{id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await WithDbAsync(db => db.JobApplications.CountAsync(Ct)));
        Assert.Equal(1, await WithDbAsync(db => db.JobPostings.CountAsync(Ct)));
        Assert.Equal(1, await WithDbAsync(db => db.Companies.CountAsync(Ct)));
    }

    [Fact]
    public async Task DeletingAnApplication_CascadesToItsAtsResult()
    {
        // Cascade on ats_results.ApplicationId: the ATS check is owned by the application
        // and means nothing without it. Seeded directly because Phase 5 is not built yet.
        var id = await Client.CreateApplicationAsync("Atlassian", "Backend Engineer", Ct);
        await WithDbAsync(async db =>
        {
            db.AtsResults.Add(new AtsResult
            {
                ApplicationId = id,
                MatchedKeywords = ["C#", "Postgres"],
                MissingMustHaveKeywords = ["Kubernetes"],
                FormattingRiskNotes = ["Avoid tables if uploading as PDF"],
            });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(1, await WithDbAsync(db => db.AtsResults.CountAsync(Ct)));

        (await Client.DeleteAsync($"/applications/{id}", Ct)).EnsureSuccessStatusCode();

        Assert.Equal(0, await WithDbAsync(db => db.AtsResults.CountAsync(Ct)));
    }

    [Fact]
    public async Task DeletingAPosting_CascadesToSkillLinksRequirementsAndAnalysis()
    {
        var id = await Client.CreateApplicationAsync("Xero", "Senior Engineer", Ct);
        (await Client.AddSkillAsync(id, "C#", Ct)).EnsureSuccessStatusCode();
        (await Client.AddRequirementAsync(id, "5+ years .NET", Ct)).EnsureSuccessStatusCode();

        var postingId = await WithDbAsync(db =>
            db.JobApplications.Where(a => a.Id == id).Select(a => a.PostingId).SingleAsync(Ct));

        await WithDbAsync(async db =>
        {
            db.AiAnalyses.Add(new AiAnalysis { PostingId = postingId, Summary = "seeded" });
            await db.SaveChangesAsync(Ct);
        });

        // The application must go first: job_applications.PostingId is Restrict, which
        // the next test pins down explicitly.
        (await Client.DeleteAsync($"/applications/{id}", Ct)).EnsureSuccessStatusCode();
        await WithDbAsync(async db =>
        {
            var posting = await db.JobPostings.SingleAsync(p => p.Id == postingId, Ct);
            db.JobPostings.Remove(posting);
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(0, await WithDbAsync(db => db.PostingSkills.CountAsync(Ct)));
        Assert.Equal(0, await WithDbAsync(db => db.JobRequirements.CountAsync(Ct)));
        Assert.Equal(0, await WithDbAsync(db => db.AiAnalyses.CountAsync(Ct)));

        // The shared skills row is Restrict, so it survives its posting being deleted.
        Assert.Equal(1, await WithDbAsync(db => db.Skills.CountAsync(Ct)));
    }

    [Fact]
    public async Task DeletingAPostingThatStillHasAnApplication_IsRefusedByTheDatabase()
    {
        var id = await Client.CreateApplicationAsync("Seek", "Engineer", Ct);
        var postingId = await WithDbAsync(db =>
            db.JobApplications.Where(a => a.Id == id).Select(a => a.PostingId).SingleAsync(Ct));

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => WithDbAsync(async db =>
        {
            db.JobPostings.Remove(await db.JobPostings.SingleAsync(p => p.Id == postingId, Ct));
            await db.SaveChangesAsync(Ct);
        }));

        Assert.Contains("violates foreign key constraint", failure.InnerException?.Message ?? "");
    }

    [Fact]
    public async Task DeletingACompanyThatStillHasPostings_IsRefusedByTheDatabase()
    {
        await Client.CreateApplicationAsync("REA Group", "Engineer", Ct);

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => WithDbAsync(async db =>
        {
            db.Companies.Remove(await db.Companies.SingleAsync(c => c.Name == "REA Group", Ct));
            await db.SaveChangesAsync(Ct);
        }));

        Assert.Contains("violates foreign key constraint", failure.InnerException?.Message ?? "");
    }

    [Fact]
    public async Task UnlinkingASkill_LeavesTheSharedSkillRowForOtherApplications()
    {
        // Phase 2.1's own stated verification step, and the payoff of the shared skills
        // table: removing C# from one application must not remove it from another.
        var first = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        var second = await Client.CreateApplicationAsync("Atlassian", "Backend Engineer", Ct);
        (await Client.AddSkillAsync(first, "C#", Ct)).EnsureSuccessStatusCode();
        (await Client.AddSkillAsync(second, "C#", Ct)).EnsureSuccessStatusCode();

        // C# must be percent-encoded: the skill name travels in the path.
        var response = await Client.DeleteAsync($"/applications/{first}/skills/C%23", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, await WithDbAsync(db => db.Skills.CountAsync(Ct)));
        Assert.Equal(1, await WithDbAsync(db => db.PostingSkills.CountAsync(Ct)));

        using var secondApp = await Client.GetApplicationAsync(second, Ct);
        var stillThere = secondApp.RootElement
            .GetProperty("posting").GetProperty("postingSkills")
            .EnumerateArray()
            .Any(ps => ps.GetProperty("skill").GetProperty("name").GetString() == "C#");
        Assert.True(stillThere);
    }
}
