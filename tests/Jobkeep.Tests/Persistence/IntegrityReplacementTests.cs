using System.Net;
using Jobkeep.Contracts.Applications;
using Jobkeep.Contracts.Match;
using Jobkeep.Contracts.Shared;
using Jobkeep.Contracts.Skills;
using Jobkeep.Modules.Ai.Domain;
using Jobkeep.Modules.Match;
using Jobkeep.Modules.Match.Domain;
using Jobkeep.Modules.Documents;
using Jobkeep.Modules.Documents.Domain;
using Jobkeep.Modules.Skills.Domain;
using Jobkeep.Tests.Documents;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Persistence;

/// <summary>
/// PHASE 13.3c — what replaced the five foreign keys 13.3b dropped, tested through the
/// routes that trigger it.
///
/// <para>
/// DeleteBehaviourTests asserts the OUTCOMES that survived: a match result dies with its
/// application, an analysis dies with its ad. This file is about the machinery, because
/// the two are no longer the same claim. A foreign key is one thing that either exists
/// or does not; a replacement is a route, a check or a subscriber, and each of those can
/// be wrong in ways a count of rows does not show — a refusal that 500s instead of 400s,
/// a message naming no number, a delete that takes a company with it.
/// </para>
///
/// <para>
/// The five, and which mechanism each got:
/// <list type="bullet">
/// <item>match_results.ApplicationId (CASCADE) — ApplicationDeleted notification.</item>
/// <item>ai_analyses.PostingId (CASCADE) — PostingDeleted notification.</item>
/// <item>job_applications.ResumeId (RESTRICT) — IApplicationContract count, at delete.</item>
/// <item>match_results.ResumeId (RESTRICT) — IMatchContract count, at delete.</item>
/// <item>resume_skills.SkillId (RESTRICT) — ISkillCatalog.FindOrCreateAsync ordering,
///       which 13.2 already shipped and ResumeSkillTests already covers.</item>
/// </list>
/// </para>
/// </summary>
public sealed class IntegrityReplacementTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    // -----------------------------------------------------------------------
    // DELETE /postings/{id} — the route that did not exist until 13.3c
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeletingAPostingWithAnApplication_Is400_NotThe500ThePlainForeignKeyWouldGive()
    {
        // The FK is still there and would still refuse this — DeleteBehaviourTests
        // proves that by going around the route. The point of the check in the handler
        // is the STATUS: an unhandled DbUpdateException is a 500, which tells a user
        // their correct request broke the server.
        var applicationId = await Client.CreateApplicationAsync("Seek", "Engineer", Ct);
        var postingId = await PostingIdAsync(applicationId);

        var response = await Client.DeleteAsync($"/postings/{postingId}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The count is in the message on purpose: "still in use" leaves the user
        // hunting, and the handler already had the number in hand.
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("1 application", body);

        Assert.Equal(1, await WithDbAsync(db => db.JobPostings.CountAsync(Ct)));
    }

    [Fact]
    public async Task ArchivingAnUnusedPosting_KEEPSItsSkillsAndRequirements_AndLeavesTheCompany()
    {
        // PHASE 8 flipped the two middle assertions. DeleteBehaviourTests carries the
        // full argument; the short version is that no DELETE reaches Postgres, so the
        // CASCADEs on posting_skills and job_requirements never fire, and the ad keeps
        // everything it needs to come back whole.
        var applicationId = await Client.CreateApplicationAsync("Xero", "Senior Engineer", Ct);
        (await Client.AddSkillAsync(applicationId, "C#", Ct)).EnsureSuccessStatusCode();
        (await Client.AddRequirementAsync(applicationId, "5+ years .NET", Ct)).EnsureSuccessStatusCode();
        var postingId = await PostingIdAsync(applicationId);

        (await Client.DeleteAsync($"/applications/{applicationId}", Ct)).EnsureSuccessStatusCode();
        var response = await Client.DeleteAsync($"/postings/{postingId}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await WithDbAsync(db => db.JobPostings.CountAsync(Ct)));
        Assert.Equal(1, await WithDbAsync(db => db.PostingSkills.CountAsync(Ct)));
        Assert.Equal(1, await WithDbAsync(db => db.JobRequirements.CountAsync(Ct)));

        // The company survives its last posting, and the shared skill survives its last
        // link. Both are RESTRICT-by-design rather than oversights: a company you once
        // applied to is a fact, and `skills` is a vocabulary, not a child table.
        Assert.Equal(1, await WithDbAsync(db => db.Companies.CountAsync(Ct)));
        Assert.Equal(1, await WithDbAsync(db => db.Skills.CountAsync(Ct)));
    }

    [Fact]
    public async Task DeletingAPostingTwice_Is404TheSecondTime()
    {
        var applicationId = await Client.CreateApplicationAsync("Canva", "Engineer", Ct);
        var postingId = await PostingIdAsync(applicationId);
        (await Client.DeleteAsync($"/applications/{applicationId}", Ct)).EnsureSuccessStatusCode();
        (await Client.DeleteAsync($"/postings/{postingId}", Ct)).EnsureSuccessStatusCode();

        var response = await Client.DeleteAsync($"/postings/{postingId}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ArchivingAPosting_OverGraphQL_KeepsTheAnalysisToo()
    {
        // The same behaviour through the other surface, and it is worth keeping for the
        // reason it was written: whatever the delete does, both surfaces must do it. It
        // used to prove that a mutation reaching the handler published the notification;
        // it now proves that neither surface publishes one, which is the same guarantee
        // pointed the other way. A GraphQL client that destroyed an analysis a REST
        // client only hid would be surface-specific behaviour — finding A4's whole
        // subject.
        var applicationId = await Client.CreateApplicationAsync("REA Group", "Engineer", Ct);
        var postingId = await PostingIdAsync(applicationId);
        await SeedAnalysisAsync(postingId);
        (await Client.DeleteAsync($"/applications/{applicationId}", Ct)).EnsureSuccessStatusCode();

        var result = await GraphQL.QueryAsync(
            "mutation ($id: UUID!) { deletePosting(id: $id) }",
            new { id = postingId });

        Assert.False(result.HasErrors, result.FirstErrorMessage);
        Assert.Equal(0, await WithDbAsync(db => db.JobPostings.CountAsync(Ct)));
        Assert.Equal(1, await WithDbAsync(db => db.AiAnalyses.CountAsync(Ct)));

        // Restored over GraphQL too, so the undo has the same parity the archive does.
        var restored = await GraphQL.QueryAsync(
            "mutation ($id: UUID!) { restorePosting(id: $id) }",
            new { id = postingId });
        Assert.False(restored.HasErrors, restored.FirstErrorMessage);
        Assert.Equal(1, await WithDbAsync(db => db.JobPostings.CountAsync(Ct)));
    }

    // -----------------------------------------------------------------------
    // DELETE /resumes/{id} — the two RESTRICTs, now asked rather than enforced
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeletingAResume_IsRefused_WhileAnApplicationWasSentWithIt()
    {
        // job_applications.ResumeId was RESTRICT until 13.3b. Nothing in Postgres says
        // so now: the refusal below is IApplicationContract.CountApplicationsForResumeAsync
        // returning 1, and the whole of the protection is that DeleteResume asks.
        var resumeId = await SeedResumeAsync("backend-focused");
        await Client.CreateApplicationAsync("Atlassian", "Backend Engineer", Ct, resumeId: resumeId);

        var response = await Client.DeleteAsync($"/resumes/{resumeId}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("backend-focused", body);   // the label, so the user knows which
        Assert.Contains("1 application", body);
        Assert.Equal(1, await WithDbAsync(db => db.Resumes.CountAsync(Ct)));
    }

    [Fact]
    public async Task DeletingAResume_IsRefused_WhileAStoredMatchCheckJudgedIt()
    {
        // match_results.ResumeId, the other dropped RESTRICT, and the one that is easy to
        // forget because the match result belongs to an application that need not name
        // this résumé at all — RunMatchCheck takes an optional resumeId, so the check can
        // have judged a different CV than the one the application was sent with.
        var resumeId = await SeedResumeAsync("ats-tuned");
        var applicationId = await Client.CreateApplicationAsync("Canva", "Engineer", Ct);
        await WithDbAsync(async db =>
        {
            db.MatchResults.Add(new MatchResult
            {
                ApplicationId = applicationId,
                ResumeId = resumeId,
                MatchedKeywords = ["C#"],
            });
            await db.SaveChangesAsync(Ct);
        });

        var response = await Client.DeleteAsync($"/resumes/{resumeId}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("1 stored match check", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, await WithDbAsync(db => db.Resumes.CountAsync(Ct)));
    }

    [Fact]
    public async Task ArchivingAnUnusedResume_KEEPSItsSkillsExperiencesAndEducations()
    {
        // PHASE 8, same flip as the posting: the three CASCADEs a résumé has are all
        // Documents' own, and none of them fire, because the DELETE that would have
        // triggered them is now an UPDATE. The parsed skills, jobs and qualifications
        // are the expensive part of a résumé — they cost a model call to produce — so a
        // restore that lost them would be a re-import wearing an undo's clothes.
        var resumeId = await SeedResumeAsync("draft");
        await WithDbAsync(async db =>
        {
            var skill = new Skill { Name = "Terraform" };
            db.Skills.Add(skill);
            await db.SaveChangesAsync(Ct);

            db.ResumeSkills.Add(new ResumeSkill
            {
                ResumeId = resumeId, SkillId = skill.Id, Source = SkillSource.Parsed,
            });
            db.ResumeExperiences.Add(new ResumeExperience
            {
                ResumeId = resumeId, Employer = "Seek", Title = "Engineer",
            });
            db.ResumeEducations.Add(new ResumeEducation
            {
                ResumeId = resumeId, Institution = "Monash", Qualification = "BSc",
            });
            await db.SaveChangesAsync(Ct);
        });

        var response = await Client.DeleteAsync($"/resumes/{resumeId}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await WithDbAsync(db => db.Resumes.CountAsync(Ct)));
        Assert.Equal(1, await WithDbAsync(db => db.ResumeSkills.CountAsync(Ct)));
        Assert.Equal(1, await WithDbAsync(db => db.ResumeExperiences.CountAsync(Ct)));
        Assert.Equal(1, await WithDbAsync(db => db.ResumeEducations.CountAsync(Ct)));

        // The shared skill row is untouched. Since 13.3b it is another module's table in
        // another schema, so this delete could not have reached it even by accident —
        // which is the property the split was for.
        Assert.Equal(1, await WithDbAsync(db => db.Skills.CountAsync(Ct)));

        // And the document comes back whole.
        (await Client.PostAsync($"/resumes/{resumeId}/restore", null, Ct))
            .EnsureSuccessStatusCode();
        Assert.Equal(1, await WithDbAsync(db => db.Resumes.CountAsync(Ct)));
    }

    [Fact]
    public async Task DeletingAResume_AfterTheApplicationUsingItIsGone_Succeeds()
    {
        // The refusal is not permanent, and this is the sequence a user actually walks:
        // delete the application, then the résumé it was sent with. It also pins that the
        // count is read at delete time rather than cached anywhere.
        var resumeId = await SeedResumeAsync("old-version");
        var applicationId = await Client.CreateApplicationAsync(
            "Atlassian", "Engineer", Ct, resumeId: resumeId);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await Client.DeleteAsync($"/resumes/{resumeId}", Ct)).StatusCode);

        (await Client.DeleteAsync($"/applications/{applicationId}", Ct)).EnsureSuccessStatusCode();

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Client.DeleteAsync($"/resumes/{resumeId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task AResumeRefusal_IsINVALID_INPUTOverGraphQL()
    {
        // Surface parity for the new refusal. The rule lives in the handler, so the two
        // surfaces cannot disagree about WHETHER to refuse; what this pins is that the
        // GraphQL edge still translates Invalid to INVALID_INPUT rather than letting it
        // arrive as an unclassified error.
        var resumeId = await SeedResumeAsync("shared");
        await Client.CreateApplicationAsync("Seek", "Engineer", Ct, resumeId: resumeId);

        var result = await GraphQL.QueryAsync(
            "mutation ($id: UUID!) { deleteResume(id: $id) }",
            new { id = resumeId });

        Assert.True(result.HasErrors);
        Assert.Equal("INVALID_INPUT", result.FirstErrorCode);
        Assert.Equal(1, await WithDbAsync(db => db.Resumes.CountAsync(Ct)));
    }

    [Fact]
    public async Task DeletingAnUnknownResume_Is404()
    {
        var response = await Client.DeleteAsync($"/resumes/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -----------------------------------------------------------------------

    private Task<Guid> PostingIdAsync(Guid applicationId)
        => WithDbAsync(db => db.JobApplications
            .Where(a => a.Id == applicationId)
            .Select(a => a.PostingId)
            .SingleAsync(Ct));

    private Task SeedAnalysisAsync(Guid postingId)
        => WithDbAsync(async db =>
        {
            db.AiAnalyses.Add(new AiAnalysis { PostingId = postingId, Summary = "seeded" });
            await db.SaveChangesAsync(Ct);
        });

    private Task<Guid> SeedResumeAsync(string label)
        => WithDbAsync(async db =>
        {
            var resume = new Resume
            {
                Label = label,
                FullName = "Tyler Ha",
                Email = "tyler@example.com",
                Location = "Melbourne",
                SourceFormat = SourceFormat.Docx,
                SourceText = new string('x', 1200),
            };
            db.Resumes.Add(resume);
            await db.SaveChangesAsync(Ct);
            return resume.Id;
        });
}
