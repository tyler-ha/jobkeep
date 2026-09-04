using System.Net;
using Jobkeep.Contracts.Applications;
using Jobkeep.Modules.Ai;
using Jobkeep.Modules.Ai.Domain;
using Jobkeep.Modules.Applications;
using Jobkeep.Modules.Match;
using Jobkeep.Modules.Match.Domain;
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
    public async Task ArchivingAnApplication_KEEPSItsMatchResult()
    {
        // THIS ASSERTION HAS BEEN 0, THEN 1, THEN 0, AND IS NOW 1 AGAIN — four phases,
        // and the churn is the useful part rather than an embarrassment.
        //
        //   * Phase 2: a CASCADE on match_results.ApplicationId. A match check is owned
        //     by its application and means nothing without it.
        //   * 13.3b: the foreign key crossed a module boundary and was dropped, so the
        //     row was orphaned. The test was inverted to assert the orphan on purpose.
        //   * 13.3c: the outcome was restored without the key — DeleteApplication
        //     published ApplicationDeleted and Match deleted the row in response. Back
        //     to 0, now proving that a module REACTED rather than that a constraint
        //     existed.
        //   * Phase 8: the application is no longer deleted. It is archived, it is one
        //     click from coming back, and DeleteApplication.cs's own 13.3c comment —
        //     "prefer the residue nobody can see" over "destroyed work on a live row" —
        //     now points the other way. So nothing is published and the check survives.
        //
        // What the assertion means at each step is different every time, and only the
        // last two are about the same question. The stored judgement outliving the
        // archive is the FEATURE: restore the application and the check you last ran is
        // still there, rather than a three-minute model call away.
        var id = await Client.CreateApplicationAsync("Atlassian", "Backend Engineer", Ct);
        await WithDbAsync(async db =>
        {
            db.MatchResults.Add(new MatchResult
            {
                ApplicationId = id,
                MatchedKeywords = ["C#", "Postgres"],
                MissingMustHaveKeywords = ["Kubernetes"],
                FormattingRiskNotes = ["Avoid tables if uploading as PDF"],
            });
            await db.SaveChangesAsync(Ct);
        });

        Assert.Equal(1, await WithDbAsync(db => db.MatchResults.CountAsync(Ct)));

        (await Client.DeleteAsync($"/applications/{id}", Ct)).EnsureSuccessStatusCode();

        // Zero LIVE applications — the test context carries the same global query filter
        // the app does, so "gone" and "archived" read identically here. That is the
        // property soft delete is built on, and it is why the six passing tests in this
        // file needed no change at all.
        Assert.Equal(0, await WithDbAsync(db => db.JobApplications.CountAsync(Ct)));

        // The row is still there, and this is the only assertion in the file that has to
        // look past the filter to say so.
        Assert.Equal(1, await WithDbAsync(db =>
            db.JobApplications.IgnoreQueryFilters().CountAsync(a => a.IsDeleted, Ct)));

        Assert.Equal(1, await WithDbAsync(db => db.MatchResults.CountAsync(Ct)));
    }

    [Fact]
    public async Task RestoringAnArchivedApplication_BringsItBackWithItsMatchResult()
    {
        // The other half, and the reason the assertion above is worth 1: an archive
        // whose undo loses the expensive part is not an undo.
        var id = await Client.CreateApplicationAsync("Atlassian", "Platform Engineer", Ct);
        await WithDbAsync(async db =>
        {
            db.MatchResults.Add(new MatchResult
            {
                ApplicationId = id,
                MatchedKeywords = ["C#"],
                MissingMustHaveKeywords = [],
                FormattingRiskNotes = [],
            });
            await db.SaveChangesAsync(Ct);
        });

        (await Client.DeleteAsync($"/applications/{id}", Ct)).EnsureSuccessStatusCode();
        var restore = await Client.PostAsync($"/applications/{id}/restore", null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
        Assert.Equal(1, await WithDbAsync(db => db.JobApplications.CountAsync(Ct)));
        Assert.Equal(1, await WithDbAsync(db => db.MatchResults.CountAsync(Ct)));
    }

    [Fact]
    public async Task ArchivingAPosting_KEEPSItsSkillLinksRequirementsAndAnalysis()
    {
        // Three rules in one test, and PHASE 8 collapsed them back into one answer after
        // 13.3c had split them across two mechanisms.
        //
        // 13.3c's arrangement: posting_skills and job_requirements are Applications' own
        // tables in its own schema, so they cascaded in Postgres; ai_analyses is not, so
        // its cascade had become a delete notification. Two mechanisms, one outcome.
        //
        // Soft delete stops BOTH. The DELETE never reaches Postgres, so the two cascades
        // never fire; and nothing is published, so the subscriber never runs. Three rows
        // survive for two different reasons that now produce the same result — which is
        // exactly what makes a restore a restore rather than a re-import.
        //
        // The delete still goes through DELETE /postings/{id} rather than
        // db.JobPostings.Remove. That mattered in 13.3c because a context delete raises
        // no event; it matters now because the route is what a user can actually reach,
        // and the interceptor makes the two paths identical anyway.
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

        // The ad itself is hidden, which is what the user asked for.
        Assert.Equal(0, await WithDbAsync(db => db.JobPostings.CountAsync(Ct)));

        // These two survive because their CASCADE never fires — there is no DELETE for
        // Postgres to cascade from. The foreign keys are untouched and still say
        // CASCADE; they are simply not consulted.
        Assert.Equal(1, await WithDbAsync(db => db.PostingSkills.CountAsync(Ct)));
        Assert.Equal(1, await WithDbAsync(db => db.JobRequirements.CountAsync(Ct)));

        // This one survives for the other reason: PostingDeleted is no longer published,
        // so Ai is never told. Same outcome, different mechanism — and both mechanisms
        // stopped because of one decision, which is the tidy part.
        Assert.Equal(1, await WithDbAsync(db => db.AiAnalyses.CountAsync(Ct)));

        // The shared skills row survives its posting either way, as it has through
        // every version of this test. It used to be a RESTRICT saying so; since 13.3b it
        // is simply a different module's table that nothing in this path can reach.
        Assert.Equal(1, await WithDbAsync(db => db.Skills.CountAsync(Ct)));

        // And the whole ad comes back intact, which is the claim the four assertions
        // above are really making.
        (await Client.PostAsync($"/postings/{postingId}/restore", null, Ct))
            .EnsureSuccessStatusCode();
        Assert.Equal(1, await WithDbAsync(db => db.JobPostings.CountAsync(Ct)));
    }

    [Fact]
    public async Task ThePostingRestrict_StillExists_EvenThoughNothingReachesItAnyMore()
    {
        // PHASE 8 REWROTE HOW THIS ASKS, not what it asserts. The rule — you cannot
        // destroy an ad while an application names it — is unchanged and the foreign key
        // that enforces it is unchanged. What changed is that the application can no
        // longer get Postgres to consider it: db.JobPostings.Remove now produces an
        // UPDATE, so the version of this test that went through the change tracker threw
        // nothing and proved nothing.
        //
        // So it goes around EF entirely. A raw DELETE is the only thing left in the
        // system that can still make this constraint speak, and keeping it provable
        // matters because the key is dormant rather than retired: a purge (F18) would
        // wake it, and a Restrict that quietly became a Cascade in the meantime would
        // eat rows on the day it did.
        //
        // The SQL names its schema — `public` holds nothing since 13.3b.
        var id = await Client.CreateApplicationAsync("Seek", "Engineer", Ct);
        var postingId = await WithDbAsync(db =>
            db.JobApplications.Where(a => a.Id == id).Select(a => a.PostingId).SingleAsync(Ct));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => WithDbAsync(db =>
            db.Database.ExecuteSqlRawAsync(
                "DELETE FROM applications.job_postings WHERE \"Id\" = {0}",
                [postingId],
                Ct)));

        Assert.Contains("violates foreign key constraint", failure.InnerException?.Message ?? failure.Message);
    }

    [Fact]
    public async Task ArchivingAPostingThatStillHasALiveApplication_IsRefusedByTheHandler()
    {
        // The refusal a user can actually reach, and since Phase 8 the ONLY one: the
        // count in DeletePostingHandler, answering 400 rather than letting a constraint
        // surface as a 500. It counts live applications, which the query filter does for
        // free — so archiving the application is what frees the ad.
        var id = await Client.CreateApplicationAsync("Seek", "Platform Engineer", Ct);
        var postingId = await WithDbAsync(db =>
            db.JobApplications.Where(a => a.Id == id).Select(a => a.PostingId).SingleAsync(Ct));

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await Client.DeleteAsync($"/postings/{postingId}", Ct)).StatusCode);

        (await Client.DeleteAsync($"/applications/{id}", Ct)).EnsureSuccessStatusCode();

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Client.DeleteAsync($"/postings/{postingId}", Ct)).StatusCode);
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
