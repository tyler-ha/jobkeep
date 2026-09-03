using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jobkeep.Contracts.Shared;
using Jobkeep.Contracts.Skills;
using Jobkeep.Modules.Documents;
using Jobkeep.Modules.Documents.Domain;
using Jobkeep.Modules.Skills.Domain;
using Jobkeep.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Tests.Documents;

/// <summary>
/// The résumé read surface added in Phase 6 step 6.1 — <c>GET /resumes</c>,
/// <c>GET /resumes/{id}</c> and the <c>DELETE</c> that undoes
/// <c>POST /resumes/{id}/skills</c>.
///
/// <para>
/// Two of these assert on things a shape test would miss. The first is that the
/// <em>list</em> never carries résumé text: that is a privacy decision
/// (<c>ListResumes.cs</c>, following the Phase 2.3 fix to the old
/// <c>ResumeText</c> column), and a decision expressed only as an absent record
/// field is one refactoring can silently reverse — so the assertion is made
/// against the raw JSON rather than against a deserialized DTO that could not
/// hold the field anyway.
/// </para>
///
/// <para>
/// The second is that removing a skill from a résumé deletes the
/// <c>resume_skills</c> join row and leaves the shared <c>skills</c> row standing.
/// That row may be linked to any number of postings, and the whole reason the
/// skills table is shared is that the ATS check's gap is a join on skill id.
/// "Take C# off my CV" must not mean "C# is no longer a skill".
/// </para>
/// </summary>
public class ResumeReadTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    /// <summary>
    /// A résumé with structure: two jobs and two qualifications, seeded in the
    /// <em>wrong</em> order on purpose so the Ordinal test is actually testing
    /// something rather than reading rows back in insertion order.
    /// </summary>
    private Task<Guid> SeedResumeAsync(
        string label,
        DateTime? updatedAtUtc = null,
        string[]? skills = null,
        bool withRecords = false)
        => WithDbAsync(async db =>
        {
            var resume = new Resume
            {
                Label = label,
                FullName = "Tyler Ha",
                Email = "tyler.ha@example.com",
                Phone = "0400 000 000",
                Location = "Melbourne",
                Headline = "Backend engineer",
                SourceFormat = SourceFormat.Docx,
                SourceText = ResumeText,
                SourceFileName = $"{label}.docx",
            };

            if (updatedAtUtc is not null)
                resume.UpdatedAtUtc = updatedAtUtc.Value;

            foreach (var name in skills ?? [])
            {
                var skill = await db.Skills.FirstOrDefaultAsync(s => s.Name == name, Ct);
                if (skill is null)
                {
                    skill = new Skill { Name = name };
                    db.Skills.Add(skill);
                }

                // 13.3b: the link carries the id, not the row. Skill.Id is assigned
                // by the property initialiser, so this works for a skill that has
                // not been saved yet exactly as the navigation used to.
                resume.ResumeSkills.Add(new ResumeSkill { SkillId = skill.Id, Source = SkillSource.Parsed });
            }

            if (withRecords)
            {
                // Ordinal 1 added first, so a query with no OrderBy has a real
                // chance of returning them the wrong way round.
                resume.Experiences.Add(new ResumeExperience
                {
                    Employer = "Second Employer",
                    Title = "Developer",
                    StartText = "Mar 2019",
                    EndText = "Feb 2021",
                    Highlights = ["Shipped a thing", "Shipped another"],
                    Ordinal = 1,
                });
                resume.Experiences.Add(new ResumeExperience
                {
                    Employer = "Current Employer",
                    Title = "Senior Developer",
                    StartText = "Mar 2021",
                    EndText = "present",
                    Highlights = ["Owns the platform"],
                    Ordinal = 0,
                });
                resume.Educations.Add(new ResumeEducation
                {
                    Institution = "Second School",
                    Qualification = "Diploma",
                    YearText = "2015",
                    Ordinal = 1,
                });
                resume.Educations.Add(new ResumeEducation
                {
                    Institution = "First School",
                    Qualification = "Master of IT",
                    YearText = "2018",
                    Ordinal = 0,
                });
            }

            db.Resumes.Add(resume);
            await db.SaveChangesAsync(Ct);
            return resume.Id;
        });

    /// <summary>
    /// Deliberately distinctive, so the "no résumé text in the list" assertion is a
    /// substring search that cannot pass by accident.
    /// </summary>
    private const string ResumeText =
        "UNIQUE-RESUME-BODY-MARKER tyler ha, melbourne, backend engineer, ten years of experience.";

    // ---------------------------------------------------------------- list

    [Fact]
    public async Task ListResumes_ReturnsNewestUpdatedFirst()
    {
        await SeedResumeAsync("older", updatedAtUtc: DateTime.UtcNow.AddDays(-3));
        await SeedResumeAsync("newer", updatedAtUtc: DateTime.UtcNow);

        var response = await Client.GetAsync("/resumes", Ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(Ct);
        var items = JsonSerializer.Deserialize<List<ResumeListItem>>(body, Json)!;

        Assert.Equal(["newer", "older"], items.Select(i => i.Label));
    }

    /// <summary>
    /// The privacy assertion. <c>ListResumes.ResumeSummary</c> has no SourceText
    /// field, so a typed test could never fail — this reads the wire.
    /// </summary>
    [Fact]
    public async Task ListResumes_DoesNotShipResumeText()
    {
        await SeedResumeAsync("mine");

        var body = await Client.GetStringAsync("/resumes", Ct);

        Assert.Contains("mine", body);
        Assert.DoesNotContain("UNIQUE-RESUME-BODY-MARKER", body);

        // And the detail does, which is what makes the omission above a decision
        // about lists rather than about résumé text in general.
        var id = await WithDbAsync(db => db.Resumes.Select(r => r.Id).FirstAsync(Ct));
        Assert.Contains("UNIQUE-RESUME-BODY-MARKER", await Client.GetStringAsync($"/resumes/{id}", Ct));
    }

    [Fact]
    public async Task ListResumes_CountsSkills_AndReportsZeroForABareResume()
    {
        await SeedResumeAsync("with-skills", skills: ["C#", "PostgreSQL", "AWS"]);
        await SeedResumeAsync("bare");

        var items = JsonSerializer.Deserialize<List<ResumeListItem>>(
            await Client.GetStringAsync("/resumes", Ct), Json)!;

        Assert.Equal(3, items.Single(i => i.Label == "with-skills").SkillCount);
        Assert.Equal(0, items.Single(i => i.Label == "bare").SkillCount);
    }

    // -------------------------------------------------------------- detail

    [Fact]
    public async Task GetResume_ReturnsExperiencesAndEducationsInDocumentOrder()
    {
        var id = await SeedResumeAsync("mine", withRecords: true);

        var detail = await Client.GetFromJsonAsync<ResumeDetailItem>($"/resumes/{id}", Json, Ct);

        Assert.NotNull(detail);
        Assert.Equal(
            ["Current Employer", "Second Employer"],
            detail!.Experiences.Select(e => e.Employer));
        Assert.Equal(
            ["First School", "Second School"],
            detail.Educations.Select(e => e.Institution));

        // The array column round-trips as an array, not as a joined string.
        Assert.Equal(["Owns the platform"], detail.Experiences[0].Highlights);
    }

    /// <summary>
    /// Phase 13.2c. The détail read no longer joins <c>skills</c> — the projection
    /// stops at <c>SkillId</c> and the names arrive through <c>ISkillCatalog</c>,
    /// which is the seam that survives the schema split.
    ///
    /// <para>
    /// What this pins is that the boundary is invisible from outside: the names are
    /// still there, still carry their category, and are still alphabetical. The sort
    /// moved out of SQL and into memory, so it is the thing most likely to be lost
    /// silently — a list that comes back in insertion order looks fine until you
    /// seed it out of order, which is what this does.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetResume_StillNamesItsSkills_AndStillSortsThemAlphabetically()
    {
        var id = await SeedResumeAsync("mine", skills: ["Rust", "AWS", "postgresql"]);

        var detail = await Client.GetFromJsonAsync<ResumeDetailItem>($"/resumes/{id}", Json, Ct);

        Assert.NotNull(detail);
        Assert.Equal(["AWS", "postgresql", "Rust"], detail!.Skills.Select(x => x.SkillName));
        Assert.All(detail.Skills, x => Assert.Equal(SkillSource.Parsed, x.Source));
    }

    /// <summary>
    /// Phase 13.2c, and a deliberate behaviour CHANGE rather than a preserved one.
    ///
    /// <para>
    /// The old query matched <c>rs.Skill.Name == name</c> exactly, and the comment
    /// above it argued for that: a loose match on the way out could delete a row the
    /// caller did not name, while <c>C#</c> and <c>c#</c> could both exist. Phase 7's
    /// unique index on <c>lower("Name")</c> made that impossible, so the objection
    /// expired and the strictness was left behind as a wart — asking to remove
    /// <c>c#</c> from a résumé that has <c>C#</c> on it returned 404.
    /// </para>
    ///
    /// <para>
    /// Routing the lookup through <c>ISkillCatalog</c>, which owns the natural key,
    /// fixed it as a side effect. Pinned here so it is a decision rather than an
    /// accident.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RemoveSkill_MatchesTheNaturalKey_SoCaseDoesNotDecideWhetherItWorks()
    {
        var id = await SeedResumeAsync("mine", skills: ["C#"]);

        var response = await Client.DeleteAsync($"/resumes/{id}/skills/c%23", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await WithDbAsync(async db =>
        {
            Assert.Equal(0, await db.ResumeSkills.CountAsync(rs => rs.ResumeId == id, Ct));
            // The shared row is untouched, and it kept the spelling it was stored with.
            Assert.Equal(1, await db.Skills.CountAsync(s => s.Name == "C#", Ct));
        });
    }

    [Fact]
    public async Task UnknownResume_Is404OverRest_AndNOT_FOUNDOverGraphQL()
    {
        var unknown = Guid.NewGuid();

        var rest = await Client.GetAsync($"/resumes/{unknown}", Ct);
        var graphql = await GraphQL.QueryAsync(
            "query ($id: UUID!) { resume(id: $id) { label } }",
            new { id = unknown });

        Assert.Equal(HttpStatusCode.NotFound, rest.StatusCode);
        Assert.Equal("NOT_FOUND", graphql.FirstErrorCode);
    }

    [Fact]
    public async Task BothReads_AreOnGraphQLToo()
    {
        var id = await SeedResumeAsync("mine", skills: ["C#"], withRecords: true);

        var list = await GraphQL.QueryAsync("{ resumes { label skillCount } }");
        Assert.False(list.HasErrors, list.FirstErrorMessage);
        Assert.Equal(1, list.Data!.Value.GetProperty("resumes")[0].GetProperty("skillCount").GetInt32());

        var detail = await GraphQL.QueryAsync(
            "query ($id: UUID!) { resume(id: $id) { label experiences { employer } skills { skillName } } }",
            new { id });
        Assert.False(detail.HasErrors, detail.FirstErrorMessage);

        var resume = detail.Data!.Value.GetProperty("resume");
        Assert.Equal("mine", resume.GetProperty("label").GetString());
        Assert.Equal("Current Employer", resume.GetProperty("experiences")[0].GetProperty("employer").GetString());
        Assert.Equal("C#", resume.GetProperty("skills")[0].GetProperty("skillName").GetString());
    }

    // -------------------------------------------------------- remove skill

    /// <summary>
    /// The one that matters: the join row goes, the shared vocabulary row stays.
    /// A posting is holding the same skill row here, which is exactly the situation
    /// <c>DeleteBehavior.Restrict</c> exists to protect.
    /// </summary>
    [Fact]
    public async Task RemoveSkill_DeletesTheLink_ButNotTheSharedSkillRow()
    {
        var id = await SeedResumeAsync("mine", skills: ["C#", "AWS"]);

        var response = await Client.DeleteAsync($"/resumes/{id}/skills/C%23", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.ResumeSkills.CountAsync(rs => rs.ResumeId == id, Ct));
            Assert.Equal(2, await db.Skills.CountAsync(Ct));
            Assert.Equal(1, await db.Skills.CountAsync(s => s.Name == "C#", Ct));
        });
    }

    [Fact]
    public async Task RemoveSkill_RoundTripsWithAdd_WhichIsTheAtsDragUndone()
    {
        var id = await SeedResumeAsync("mine");

        var added = await Client.PostAsJsonAsync(
            $"/resumes/{id}/skills", new { skillName = ".NET" }, Ct);
        added.EnsureSuccessStatusCode();
        await WithDbAsync(async db => Assert.Equal(1, await db.ResumeSkills.CountAsync(Ct)));

        var removed = await Client.DeleteAsync($"/resumes/{id}/skills/.NET", Ct);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        await WithDbAsync(async db =>
        {
            Assert.Equal(0, await db.ResumeSkills.CountAsync(Ct));
            // The skill the user briefly claimed is still in the vocabulary — it is
            // very likely on a posting, which is how it got suggested in the first place.
            Assert.Equal(1, await db.Skills.CountAsync(s => s.Name == ".NET", Ct));
        });
    }

    [Fact]
    public async Task RemoveSkill_DistinguishesAnUnknownResumeFromAnUnlinkedSkill()
    {
        var id = await SeedResumeAsync("mine", skills: ["C#"]);

        var unknownResume = await Client.DeleteAsync($"/resumes/{Guid.NewGuid()}/skills/C%23", Ct);
        var notLinked = await Client.DeleteAsync($"/resumes/{id}/skills/Kubernetes", Ct);

        Assert.Equal(HttpStatusCode.NotFound, unknownResume.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notLinked.StatusCode);
        Assert.Contains("not found", await unknownResume.Content.ReadAsStringAsync(Ct));
        Assert.Contains("is not on resume", await notLinked.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task RemoveSkill_IsOnGraphQLToo()
    {
        var id = await SeedResumeAsync("mine", skills: ["C#"]);

        var result = await GraphQL.QueryAsync(
            """
            mutation ($id: UUID!) {
              removeSkillFromResume(resumeId: $id, skillName: "C#")
            }
            """,
            new { id });

        Assert.False(result.HasErrors, result.FirstErrorMessage);
        await WithDbAsync(async db => Assert.Equal(0, await db.ResumeSkills.CountAsync(Ct)));
    }

    // Local mirrors of the response DTOs. Declared here rather than referencing the
    // production records so that a change to the wire shape shows up as a failing
    // test rather than as a test that silently follows it.
    private sealed record ResumeListItem(Guid Id, string Label, string? FullName, int SkillCount);

    private sealed record ResumeDetailItem(
        Guid Id,
        string Label,
        string SourceText,
        List<ResumeSkillItem> Skills,
        List<ExperienceItem> Experiences,
        List<EducationItem> Educations);

    private sealed record ResumeSkillItem(string SkillName, string? Category, SkillSource Source);

    private sealed record ExperienceItem(string Employer, string? Title, List<string> Highlights, int Ordinal);

    private sealed record EducationItem(string Institution, string? Qualification, int Ordinal);
}
