using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jobkeep.Contracts.Shared;
using Jobkeep.Modules.Applications;
using Jobkeep.Modules.Documents.Domain;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Persistence;

/// <summary>
/// PHASE 8 — soft delete. What an archive hides, what it keeps, and the two places
/// it can silently fail to hide anything.
///
/// <para>
/// DeleteBehaviourTests and IntegrityReplacementTests already cover the delete
/// paths, flipped in place where the phase changed what they mean. This file is
/// the part that had no test to flip, and it is deliberately weighted towards the
/// failures that are INVISIBLE rather than the ones a user would notice:
/// </para>
///
/// <list type="bullet">
///   <item>The three published VIEWS. A global query filter is an EF construct;
///   a view is SQL Postgres runs on its own, so <c>HasQueryFilter</c> does not
///   reach it. An Insights page quietly counting archived applications looks
///   exactly like one counting live ones, which is the worst shape a bug can
///   have. This is the reason the migration hand-writes three CREATE OR REPLACE
///   statements, and these are the tests that would fail if it stopped.</item>
///   <item>The FILTERED unique index on <c>resumes.LabelNormalized</c>. Without
///   the predicate, archiving a résumé burns its label forever — and the failure
///   surfaces later, on an unrelated import, as a constraint naming a document
///   the user cannot see.</item>
/// </list>
///
/// <para>
/// Both are integration tests against real Postgres and could not be anything
/// else. A fake would apply the C# filter to everything and report both defects
/// as fixed, which is the exact case CLAUDE.md means by "the bugs this project
/// actually has are invisible to fakes".
/// </para>
/// </summary>
public sealed class SoftDeleteTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    // ------------------------------------------------------------------
    // The row is hidden, not gone
    // ------------------------------------------------------------------

    [Fact]
    public async Task AnArchivedApplication_IsHiddenFromEveryRead_ButStillOnDisk()
    {
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        (await Client.DeleteAsync($"/applications/{id}", Ct)).EnsureSuccessStatusCode();

        // Gone from the list, gone from the detail read, and a second archive is a
        // 404 — the query filter hides it from the handler's own lookup, so the
        // slice cannot re-stamp DeletedAtUtc and quietly move the archive date.
        Assert.Empty((await ListAsync()).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/applications/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync($"/applications/{id}", Ct)).StatusCode);

        // Still there, with both columns set. IgnoreQueryFilters is the only way to
        // ask, which is the property that makes the filter worth trusting.
        var archived = await WithDbAsync(db => db.JobApplications
            .IgnoreQueryFilters()
            .SingleAsync(a => a.Id == id, Ct));

        Assert.True(archived.IsDeleted);
        Assert.NotNull(archived.DeletedAtUtc);
    }

    [Fact]
    public async Task Restoring_BringsItBack_AndRestoringALiveRowIsA404()
    {
        var id = await Client.CreateApplicationAsync("Atlassian", "Engineer", Ct);
        (await Client.DeleteAsync($"/applications/{id}", Ct)).EnsureSuccessStatusCode();

        (await Client.PostAsync($"/applications/{id}/restore", null, Ct)).EnsureSuccessStatusCode();

        Assert.Single((await ListAsync()).Items);

        var restored = await WithDbAsync(db => db.JobApplications.SingleAsync(a => a.Id == id, Ct));
        Assert.False(restored.IsDeleted);
        Assert.Null(restored.DeletedAtUtc);

        // A live row is not in the archive, so restoring it addresses nothing.
        // RestoreApplication.cs argues why that is a 404 rather than a no-op 200.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client.PostAsync($"/applications/{id}/restore", null, Ct)).StatusCode);
    }

    // ------------------------------------------------------------------
    // includeArchived — include, not only
    // ------------------------------------------------------------------

    [Fact]
    public async Task IncludeArchived_ReturnsBothKinds_AndFlagsWhichIsWhich()
    {
        var live = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        var archived = await Client.CreateApplicationAsync("Seek", "Data Engineer", Ct);
        (await Client.DeleteAsync($"/applications/{archived}", Ct)).EnsureSuccessStatusCode();

        var page = await ListAsync("?includeArchived=true");

        // BOTH, which is what the words "include archived" mean to the person
        // ticking the box. An archived-only view would be a third state nobody
        // asked for; a client that wants it filters the list it already has.
        Assert.Equal(2, page.TotalCount);

        var byId = page.Items.ToDictionary(
            i => i.GetProperty("id").GetGuid(),
            i => i.GetProperty("isArchived").GetBoolean());

        Assert.False(byId[live]);
        Assert.True(byId[archived]);

        // And the flag is false for everything when the caller did not ask, so a
        // client cannot mistake the default page for a mixed one.
        Assert.All((await ListAsync()).Items, i => Assert.False(i.GetProperty("isArchived").GetBoolean()));
    }

    [Fact]
    public async Task IncludeArchived_StillReturnsAnApplicationWhoseAdWasArchivedToo()
    {
        // The join hazard, and the reason ListApplications calls IgnoreQueryFilters
        // on the whole query rather than adding an OR to one predicate.
        //
        // job_postings carries the filter as well, so an application whose ad is
        // also archived would be dropped by the INNER JOIN behind
        // `a.Posting.Company.Name` — silently, and only for that one row. A caller
        // who asked to see archived applications would get a page missing some of
        // them with nothing to indicate anything had been withheld.
        //
        // Archiving the application first and its ad second is a legal sequence:
        // the ad refuses only while a LIVE application names it.
        var id = await Client.CreateApplicationAsync("REA Group", "Engineer", Ct);
        var postingId = await WithDbAsync(db =>
            db.JobApplications.Where(a => a.Id == id).Select(a => a.PostingId).SingleAsync(Ct));

        (await Client.DeleteAsync($"/applications/{id}", Ct)).EnsureSuccessStatusCode();
        (await Client.DeleteAsync($"/postings/{postingId}", Ct)).EnsureSuccessStatusCode();

        var page = await ListAsync("?includeArchived=true");

        var item = Assert.Single(page.Items);
        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal("REA Group", item.GetProperty("company").GetString());
    }

    // ------------------------------------------------------------------
    // The three published views — the silent half of the phase
    // ------------------------------------------------------------------

    [Fact]
    public async Task TheStatusFunnel_DoesNotCountArchivedApplications()
    {
        await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        var archived = await Client.CreateApplicationAsync("Seek", "Data Engineer", Ct);
        (await Client.DeleteAsync($"/applications/{archived}", Ct)).EnsureSuccessStatusCode();

        // Read through the API rather than the view, because the API is what a user
        // sees — and because the whole failure mode here is a number that looks
        // right. `applied` is the stage CreateApplication starts every row in.
        using var funnel = JsonDocument.Parse(
            await Client.GetStringAsync("/stats/funnel", Ct));

        var applied = funnel.RootElement.GetProperty("stages")
            .EnumerateArray()
            .Single(s => s.GetProperty("status").GetString() == nameof(ApplicationStatus.Applied));

        Assert.Equal(1, applied.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task TheCompanyRollup_DoesNotCountArchivedApplications_NorArchivedAds()
    {
        // Two predicates in that view, so two ways for it to be wrong. Canva keeps a
        // live application; Seek's is archived; REA Group's is archived along with
        // its ad, which is the second predicate's case and the one a single
        // `WHERE NOT a."IsDeleted"` would have missed.
        await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        var seek = await Client.CreateApplicationAsync("Seek", "Data Engineer", Ct);
        (await Client.DeleteAsync($"/applications/{seek}", Ct)).EnsureSuccessStatusCode();

        var rea = await Client.CreateApplicationAsync("REA Group", "Engineer", Ct);
        var reaPosting = await WithDbAsync(db =>
            db.JobApplications.Where(a => a.Id == rea).Select(a => a.PostingId).SingleAsync(Ct));
        (await Client.DeleteAsync($"/applications/{rea}", Ct)).EnsureSuccessStatusCode();
        (await Client.DeleteAsync($"/postings/{reaPosting}", Ct)).EnsureSuccessStatusCode();

        using var rollup = JsonDocument.Parse(
            await Client.GetStringAsync("/stats/companies", Ct));

        var rows = rollup.RootElement.EnumerateArray().ToList();
        var only = Assert.Single(rows);
        Assert.Equal("Canva", only.GetProperty("name").GetString());
        Assert.Equal(1, only.GetProperty("applicationCount").GetInt32());
    }

    [Fact]
    public async Task SkillDemand_CountsAdsRatherThanApplications_AndDropsArchivedAds()
    {
        // The one view whose predicate needed a JOIN it did not have, because
        // posting_skills carries no IsDeleted of its own.
        //
        // The pairing is the point: C# is on both ads and must fall from 2 to 1
        // when one ad is archived, while Go is on the archived ad only and must
        // disappear entirely. A missing JOIN passes neither.
        var live = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        var doomed = await Client.CreateApplicationAsync("Seek", "Data Engineer", Ct);
        (await Client.AddSkillAsync(live, "C#", Ct)).EnsureSuccessStatusCode();
        (await Client.AddSkillAsync(doomed, "C#", Ct)).EnsureSuccessStatusCode();
        (await Client.AddSkillAsync(doomed, "Go", Ct)).EnsureSuccessStatusCode();

        var doomedPosting = await WithDbAsync(db =>
            db.JobApplications.Where(a => a.Id == doomed).Select(a => a.PostingId).SingleAsync(Ct));
        (await Client.DeleteAsync($"/applications/{doomed}", Ct)).EnsureSuccessStatusCode();
        (await Client.DeleteAsync($"/postings/{doomedPosting}", Ct)).EnsureSuccessStatusCode();

        using var demand = JsonDocument.Parse(
            await Client.GetStringAsync("/stats/skill-demand", Ct));

        var counts = demand.RootElement.EnumerateArray()
            .ToDictionary(
                s => s.GetProperty("name").GetString()!,
                s => s.GetProperty("postingCount").GetInt32());

        Assert.Equal(1, counts["C#"]);
        Assert.False(counts.ContainsKey("Go"));
    }

    [Fact]
    public async Task ArchivingOnlyTheApplication_LeavesTheAdCountedForSkillDemand()
    {
        // The other side of the same view, and the assertion that stops someone
        // "fixing" the JOIN by extending it to job_applications.
        //
        // Skill demand measures what the MARKET asks for, not what you applied to.
        // An ad whose application you archived is still an ad you saw, so its
        // skills still count — the same reasoning that lets an ad you never applied
        // to appear at all.
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        (await Client.AddSkillAsync(id, "C#", Ct)).EnsureSuccessStatusCode();

        (await Client.DeleteAsync($"/applications/{id}", Ct)).EnsureSuccessStatusCode();

        using var demand = JsonDocument.Parse(
            await Client.GetStringAsync("/stats/skill-demand", Ct));

        var only = Assert.Single(demand.RootElement.EnumerateArray());
        Assert.Equal("C#", only.GetProperty("name").GetString());
        Assert.Equal(1, only.GetProperty("postingCount").GetInt32());
    }

    // ------------------------------------------------------------------
    // The filtered unique index on resumes.LabelNormalized
    // ------------------------------------------------------------------

    [Fact]
    public async Task ArchivingAResume_FreesItsLabel_AndTheRestoreIsThenRefused()
    {
        // The gotcha the phase doc singles out, in one test because the two halves
        // are one decision. A plain unique index would fail the first assertion; a
        // filtered one buys it and costs the last.
        var first = await SeedResumeAsync("Backend");
        (await Client.DeleteAsync($"/resumes/{first}", Ct)).EnsureSuccessStatusCode();

        // Free, and free case-insensitively — the index sits on the generated
        // lower("Label") column, so this also pins that Phase 7's natural key and
        // Phase 8's predicate ended up on the SAME index rather than on two.
        var second = await SeedResumeAsync("backend");

        // Now the archived one cannot come back, and it is refused with a sentence
        // rather than by a constraint surfacing as a 500.
        var refused = await Client.PostAsync($"/resumes/{first}/restore", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("already called", await refused.Content.ReadAsStringAsync(Ct));

        // Free the name and the restore succeeds, which is what makes the refusal a
        // conflict rather than a dead end.
        (await Client.DeleteAsync($"/resumes/{second}", Ct)).EnsureSuccessStatusCode();
        (await Client.PostAsync($"/resumes/{first}/restore", null, Ct)).EnsureSuccessStatusCode();

        var live = await WithDbAsync(db => db.Resumes.SingleAsync(Ct));
        Assert.Equal(first, live.Id);
        Assert.Equal("Backend", live.Label);
    }

    [Fact]
    public async Task ArchivedResumes_AreHiddenFromTheShelf_UnlessAskedFor()
    {
        await SeedResumeAsync("generalist");
        var archived = await SeedResumeAsync("backend");
        (await Client.DeleteAsync($"/resumes/{archived}", Ct)).EnsureSuccessStatusCode();

        Assert.Single(await ResumesAsync());

        var both = await ResumesAsync("?includeArchived=true");
        Assert.Equal(2, both.Length);
        Assert.Single(both, r => r.GetProperty("isArchived").GetBoolean());
    }

    // ------------------------------------------------------------------
    // Both surfaces, one rule
    // ------------------------------------------------------------------

    [Fact]
    public async Task Restore_BehavesIdenticallyOverGraphQL()
    {
        // Parity, for the reason SurfaceParityTests exists: an operation on one
        // surface and not the other is how the two start enforcing different
        // things. A GraphQL client that can archive but not restore would be able
        // to reach a state it cannot leave.
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        var deleted = await GraphQL.QueryAsync(
            "mutation ($id: UUID!) { deleteApplication(id: $id) }", new { id });
        Assert.False(deleted.HasErrors, deleted.FirstErrorMessage);
        Assert.Empty((await ListAsync()).Items);

        var restored = await GraphQL.QueryAsync(
            "mutation ($id: UUID!) { restoreApplication(id: $id) }", new { id });
        Assert.False(restored.HasErrors, restored.FirstErrorMessage);
        Assert.Single((await ListAsync()).Items);

        // And restoring a live row errors on this surface too, rather than
        // answering `false` — the distinction 13.3c drew about the delete.
        var again = await GraphQL.QueryAsync(
            "mutation ($id: UUID!) { restoreApplication(id: $id) }", new { id });
        Assert.True(again.HasErrors);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private sealed record Page(List<JsonElement> Items, int TotalCount);

    private async Task<Page> ListAsync(string query = "")
    {
        using var doc = JsonDocument.Parse(await Client.GetStringAsync($"/applications{query}", Ct));
        return new Page(
            doc.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList(),
            doc.RootElement.GetProperty("totalCount").GetInt32());
    }

    private async Task<JsonElement[]> ResumesAsync(string query = "")
    {
        using var doc = JsonDocument.Parse(await Client.GetStringAsync($"/resumes{query}", Ct));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private Task<Guid> SeedResumeAsync(string label)
        => WithDbAsync(async db =>
        {
            var resume = new Resume
            {
                Label = label,
                FullName = "Tyler Ha",
                SourceFormat = SourceFormat.Docx,
                SourceText = new string('x', 1200),
            };
            db.Resumes.Add(resume);
            await db.SaveChangesAsync(Ct);
            return resume.Id;
        });
}
