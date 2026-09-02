using System.Net;
using Jobkeep.Models;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Persistence;

/// <summary>
/// The per-relationship delete behaviour configured in each module's
/// IEntityTypeConfiguration.
///
/// docs/architecture.md §4 lists "deliberate delete behaviour" as something the schema
/// already gets right, which is exactly the kind of claim that rots silently — nothing
/// fails a build when a Restrict quietly becomes a Cascade and starts eating rows.
///
/// <para>
/// PHASE 13.3b DROPPED FIVE FOREIGN KEYS, and two of the rules below went with them.
/// Both tests were kept and INVERTED rather than deleted, asserting the orphan the
/// split created — the same thing this suite did with the case-sensitive skill dedup,
/// because a defect written down as a passing test is visible and breaks loudly on the
/// change that fixes it. <b>13.3c is that change and both are flipped back.</b> The
/// rules they assert are identical to the ones the foreign keys enforced; what differs
/// is that a delete notification now enforces them, so both tests reach the database
/// through a ROUTE rather than through the test context. That is not incidental —
/// removing a row directly, as the posting test used to, bypasses the publisher and
/// would prove nothing about the replacement.
/// </para>
///
/// <para>
/// The three rules that SURVIVE untouched are the three that never crossed a module
/// boundary: job_applications to job_postings, job_postings to companies, and a
/// posting's own skill links and requirements. That contrast is the phase in one file.
/// IntegrityReplacementTests.cs covers what 13.3c added around them — the refusals,
/// the new routes, and the orphans that are now impossible.
/// </para>
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
    public async Task DeletingAnApplication_TakesItsAtsResultWithIt()
    {
        // WAS a cascade on ats_results.ApplicationId, on the grounds that an ATS check is
        // owned by its application and means nothing without it. That is still true, and
        // since 13.3b Postgres can no longer enforce it: `ats_results` is in the Ats
        // module's schema and the foreign key crossed a boundary.
        //
        // 13.3c restored the OUTCOME without the key. DeleteApplication publishes
        // ApplicationDeleted after it commits; Ats subscribes with OnApplicationDeleted
        // and deletes the row. So this assertion is 0 again, and the interesting part is
        // what the test now proves: not that a constraint exists, but that a module
        // reacted. Between 13.3b and 13.3c it asserted the orphan on purpose.
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

        Assert.Equal(0, await WithDbAsync(db => db.JobApplications.CountAsync(Ct)));
        Assert.Equal(0, await WithDbAsync(db => db.AtsResults.CountAsync(Ct)));
    }

    [Fact]
    public async Task DeletingAPosting_CascadesToSkillLinksAndRequirements_AndTakesItsAnalysis()
    {
        // Three rules in one test, and 13.3c split them across two mechanisms.
        // posting_skills and job_requirements are Applications' own tables in
        // Applications' own schema, so they still cascade in Postgres. ai_analyses is
        // not, so its cascade is now a delete notification: DeletePosting publishes
        // PostingDeleted and Ai's OnPostingDeleted removes the row.
        //
        // Which is why the delete below goes through DELETE /postings/{id} rather than
        // db.JobPostings.Remove. Between 13.3b and 13.3c it went through the context,
        // because no route existed — and a context delete raises no event, so the same
        // arrangement would still leave the analysis behind and the test would be
        // asserting the absence of a route rather than the presence of a replacement.
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
        // the next test pins down explicitly and DeletePosting turns into a 400 rather
        // than letting it surface as a 500.
        (await Client.DeleteAsync($"/applications/{id}", Ct)).EnsureSuccessStatusCode();
        (await Client.DeleteAsync($"/postings/{postingId}", Ct)).EnsureSuccessStatusCode();

        // These two still cascade: both tables are Applications' own, in Applications'
        // schema, so the foreign keys survived 13.3b untouched.
        Assert.Equal(0, await WithDbAsync(db => db.PostingSkills.CountAsync(Ct)));
        Assert.Equal(0, await WithDbAsync(db => db.JobRequirements.CountAsync(Ct)));

        // This one goes for a different reason. `ai_analyses` is the Ai module's schema
        // and its FK to job_postings has been gone since 13.3b, so nothing in Postgres
        // connects these two rows; the analysis is deleted because Ai was told the ad
        // was, and chose to.
        Assert.Equal(0, await WithDbAsync(db => db.AiAnalyses.CountAsync(Ct)));

        // The shared skills row survives its posting either way. It used to be a
        // RESTRICT saying so; now it is simply a different module's table that nothing
        // in this delete path can reach.
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
            .GetProperty("posting").GetProperty("skills")
            .EnumerateArray()
            .Any(ps => ps.GetProperty("skillName").GetString() == "C#");
        Assert.True(stillThere);
    }
}
