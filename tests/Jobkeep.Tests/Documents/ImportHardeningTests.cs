using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jobkeep.Models;
using Jobkeep.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Tests.Documents;

/// <summary>
/// Phase 4.5 — the defects the pre-merge review found, each pinned by the request
/// that used to break.
///
/// <para>
/// Separate from <see cref="ImportTests"/> on purpose. That file describes what
/// the feature is FOR: upload, review, confirm, and the gate between them. This
/// one describes what it survives, and every test here is a regression rather
/// than a specification — each one failed against the commit the review started
/// from.
/// </para>
///
/// <para>
/// The shape they share is worth naming, because it is the argument for having
/// reviewed a module nobody had read: an import carries content from three
/// sources with three different trust levels — a document, a language model, and
/// a person — and almost every defect was a place where the code treated one of
/// them like another. The model's output was trusted to fit the columns. The
/// deserializer's output was trusted to honour the C# types. A foreign key was
/// trusted to point at something.
/// </para>
/// </summary>
public class ImportHardeningTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private HttpClient AppWithModel(string json) =>
        Fixture.App
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddSingleton<IChatClient>(new FakeChatClient(json))))
            .CreateClient();

    private const string ResumeReply = """
        {
          "fullName": "Jane Doe",
          "email": "jane.doe@example.com",
          "phone": "+61 400 000 000",
          "location": "Melbourne, Australia",
          "headline": "Backend engineer.",
          "skills": ["C#"],
          "experience": [
            {
              "employer": "Canva",
              "title": "Senior Engineer",
              "start": "Mar 2021",
              "end": "Present",
              "highlights": ["Payments."]
            }
          ],
          "education": []
        }
        """;

    private static byte[] FixtureBytes(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static MultipartFormDataContent Upload(
        byte[] bytes, string fileName, DocumentKind kind, string? label = null)
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "file", fileName },
            { new StringContent(kind.ToString()), "kind" }
        };
        if (label is not null) form.Add(new StringContent(label), "label");
        return form;
    }

    private async Task<Guid> ImportAsync(
        HttpClient client, string fixtureName, DocumentKind kind, string? label = null)
    {
        var response = await client.PostAsync(
            "/imports", Upload(FixtureBytes(fixtureName), fixtureName, kind, label), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("id").GetGuid();
    }

    // ----------------------------------------------- null collections in a PUT

    [Fact]
    public async Task Review_AcceptsADraftWithOmittedLists_AndConfirmStillCommits()
    {
        // The highest-severity finding, and the one worth remembering as a general
        // fact rather than a bug: System.Text.Json does NOT enforce
        // nullable-reference-type annotations. `List<string> Skills` on a record
        // is a compile-time claim with no runtime enforcement, so a body that
        // omits "skills" deserializes to null, is stored as null, and the confirm
        // that reads it back throws NullReferenceException — a 500 on a request
        // whose only sin was leaving a field out.
        //
        // A client sending only the fields it changed is not misbehaving. It is
        // the most ordinary thing a client does.
        var client = AppWithModel(ResumeReply);
        var id = await ImportAsync(client, "resume.pdf", DocumentKind.Resume);

        var sparse = new
        {
            resume = new
            {
                label = "sparse",
                fullName = "Jane Doe",
                // skills, experience and education are all absent.
            },
            posting = (object?)null
        };

        var review = await client.PutAsJsonAsync($"/imports/{id}", sparse, Ct);
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);

        // Stored as empty lists, not as JSON nulls — so anything reading this row
        // later gets a list it can enumerate.
        await WithDbAsync(async db =>
        {
            var import = await db.DocumentImports.SingleAsync(Ct);
            Assert.DoesNotContain("\"skills\":null", import.DraftJson);
            Assert.DoesNotContain("\"experience\":null", import.DraftJson);
        });

        var confirm = await client.PostAsync($"/imports/{id}/confirm", null, Ct);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        await WithDbAsync(async db =>
        {
            var resume = await db.Resumes
                .Include(r => r.ResumeSkills)
                .Include(r => r.Experiences)
                .SingleAsync(Ct);
            Assert.Equal("sparse", resume.Label);
            Assert.Empty(resume.ResumeSkills);
            Assert.Empty(resume.Experiences);
        });
    }

    [Fact]
    public async Task Review_AcceptsAnExperienceWithNoHighlights()
    {
        // The same defect one level down. Sanitising the top-level lists is not
        // enough if the objects inside them carry lists of their own.
        var client = AppWithModel(ResumeReply);
        var id = await ImportAsync(client, "resume.pdf", DocumentKind.Resume);

        var draft = new
        {
            resume = new
            {
                label = "no-highlights",
                skills = new[] { "C#" },
                experience = new[] { new { employer = "Canva", title = "Engineer" } },
                education = Array.Empty<object>()
            },
            posting = (object?)null
        };

        (await client.PutAsJsonAsync($"/imports/{id}", draft, Ct)).EnsureSuccessStatusCode();
        var confirm = await client.PostAsync($"/imports/{id}/confirm", null, Ct);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        await WithDbAsync(async db =>
        {
            var experience = await db.ResumeExperiences.SingleAsync(Ct);
            Assert.Equal("Canva", experience.Employer);
            Assert.Empty(experience.Highlights);
        });
    }

    // ---------------------------------------------------------- column widths

    [Fact]
    public async Task Confirm_ClipsModelOutputToTheColumnWidth_RatherThanReturning500()
    {
        // A model asked to copy a job title out of a resume will occasionally
        // return the whole line it found it on. Postgres answers varchar(200)
        // overflow with 22001, which surfaced as an unhandled DbUpdateException:
        // a 500 for a document that parsed fine, caused by nothing the user did
        // and fixable by nothing the user can do.
        var overlong = new string('x', 400);
        var reply = $$"""
            {
              "fullName": "{{overlong}}",
              "email": null,
              "phone": null,
              "location": null,
              "headline": null,
              "skills": [],
              "experience": [
                { "employer": "{{overlong}}", "title": null, "start": null,
                  "end": null, "highlights": [] }
              ],
              "education": []
            }
            """;

        var client = AppWithModel(reply);
        var id = await ImportAsync(client, "resume.pdf", DocumentKind.Resume, label: "clipped");

        var confirm = await client.PostAsync($"/imports/{id}/confirm", null, Ct);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        await WithDbAsync(async db =>
        {
            var resume = await db.Resumes.Include(r => r.Experiences).SingleAsync(Ct);
            Assert.Equal(200, resume.FullName!.Length);
            Assert.Equal(200, resume.Experiences.Single().Employer.Length);
        });
    }

    [Fact]
    public async Task Upload_RefusesALabelWiderThanTheColumn_RatherThanTruncatingIt()
    {
        // The asymmetry with the test above is the decision being pinned: model
        // output is clipped, a label the USER typed is refused. Silently storing
        // something other than what someone wrote is worse than telling them it
        // is too long, and a clipped label can collide with an existing one under
        // the uniqueness rule.
        var client = AppWithModel(ResumeReply);

        var response = await client.PostAsync(
            "/imports",
            Upload(FixtureBytes("resume.pdf"), "resume.pdf", DocumentKind.Resume,
                label: new string('l', 150)),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("150 characters", await response.Content.ReadAsStringAsync(Ct));

        // Refused before anything was stored.
        await WithDbAsync(async db => Assert.Equal(0, await db.DocumentImports.CountAsync(Ct)));
    }

    [Fact]
    public async Task Confirm_RefusesALabelWiderThanTheColumn_WhenItArrivesViaReview()
    {
        // The same rule at the other gate. Upload is not the only way a label
        // gets set — the review screen is where the user is *expected* to change
        // it — so the check has to exist on both paths or it exists on neither.
        var client = AppWithModel(ResumeReply);
        var id = await ImportAsync(client, "resume.pdf", DocumentKind.Resume);

        var draft = new
        {
            resume = new
            {
                label = new string('l', 150),
                skills = Array.Empty<string>(),
                experience = Array.Empty<object>(),
                education = Array.Empty<object>()
            },
            posting = (object?)null
        };

        (await client.PutAsJsonAsync($"/imports/{id}", draft, Ct)).EnsureSuccessStatusCode();

        var confirm = await client.PostAsync($"/imports/{id}/confirm", null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Contains("150 characters", await confirm.Content.ReadAsStringAsync(Ct));

        await WithDbAsync(async db => Assert.Equal(0, await db.Resumes.CountAsync(Ct)));
    }

    [Fact]
    public async Task Upload_ShortensALongFilenameIntoAUsableDefaultLabel()
    {
        // A 200-character filename is ordinary; resumes.Label is varchar(100).
        // The default nobody typed should not be the thing that turns confirm
        // into a database error, so this one IS clipped.
        var client = AppWithModel(ResumeReply);
        var longName = new string('f', 250) + ".pdf";

        var response = await client.PostAsync(
            "/imports", Upload(FixtureBytes("resume.pdf"), longName, DocumentKind.Resume), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var id = body.RootElement.GetProperty("id").GetGuid();

        var confirm = await client.PostAsync($"/imports/{id}/confirm", null, Ct);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        await WithDbAsync(async db =>
        {
            var resume = await db.Resumes.SingleAsync(Ct);
            Assert.True(resume.Label.Length <= 100, $"label was {resume.Label.Length}");
        });
    }

    [Fact]
    public async Task Upload_KeepsTheLabel_EvenWhenTheModelFails()
    {
        // The label used to be dropped on the degraded path: the empty draft
        // written when the model fails carried no label at all, so a user who
        // typed one at upload, then hit an Ollama outage, lost it. Since the
        // whole point of saving the extraction first is that /reparse costs no
        // re-upload, losing the user's own input on that path defeats it.
        var client = AppWithModel("{ this is not json");
        var id = await ImportAsync(client, "resume.pdf", DocumentKind.Resume, label: "survives");

        await WithDbAsync(async db =>
        {
            var import = await db.DocumentImports.SingleAsync(Ct);
            Assert.Contains("survives", import.DraftJson);
        });

        var read = await client.GetAsync($"/imports/{id}", Ct);
        read.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct));
        Assert.Equal("survives",
            body.RootElement.GetProperty("draft").GetProperty("resume")
                .GetProperty("label").GetString());
    }

    // ------------------------------------------------------- the posting half

    [Fact]
    public async Task Confirm_ForAPosting_ReportsRequirementsItCouldNotSave()
    {
        // The commit used to drop rejected requirements in silence: the response
        // said the application was logged, and a requirement the review screen
        // had shown simply was not there. A count the user can check against what
        // they saw is the difference between a partial success and an invisible
        // one.
        //
        // The blank arrives through the REVIEW screen rather than from the model,
        // and that is not a contrivance - it is the only way one can get this
        // far. ImportDraft filters blank requirements out of MODEL output when it
        // builds the draft, so the case that survives to the commit is the user
        // leaving a bullet empty while editing. Trying to provoke this with a
        // model reply is what the first version of this test did, and it passed
        // vacuously.
        var reply = """
            {
              "company": "Atlassian",
              "title": "Senior Backend Engineer",
              "location": null,
              "skills": [],
              "requirements": [
                { "text": "Five years building backend services.",
                  "kind": "qualification", "mustHave": true }
              ]
            }
            """;

        var client = AppWithModel(reply);
        var id = await ImportAsync(client, "resume.txt", DocumentKind.JobPosting);

        var edited = new
        {
            resume = (object?)null,
            posting = new
            {
                company = "Atlassian",
                title = "Senior Backend Engineer",
                skills = Array.Empty<object>(),
                requirements = new[]
                {
                    new { text = "Five years building backend services.",
                          kind = "Qualification", isMustHave = true },
                    new { text = "   ", kind = "Responsibility", isMustHave = false },
                }
            }
        };

        (await client.PutAsJsonAsync($"/imports/{id}", edited, Ct)).EnsureSuccessStatusCode();

        var confirm = await client.PostAsync($"/imports/{id}/confirm", null, Ct);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var message = await confirm.Content.ReadAsStringAsync(Ct);
        Assert.Contains("1 requirement was rejected", message);

        await WithDbAsync(async db =>
        {
            // One application, and only the requirement that was actually valid.
            Assert.Equal(1, await db.JobApplications.CountAsync(Ct));
            var requirement = await db.JobRequirements.SingleAsync(Ct);
            Assert.Contains("Five years", requirement.Text);
        });
    }

    // ------------------------------------------- resumeId as a real foreign key

    [Fact]
    public async Task CreateApplication_RefusesAnUnknownResumeId_WithA400()
    {
        // resumeId was taken on trust and handed to EF, so a client typo came
        // back as a foreign-key violation inside an unhandled DbUpdateException:
        // a 500 describing a constraint name. This is the same class of check
        // every other reference in the app already had.
        var response = await Client.PostAsJsonAsync("/applications", new
        {
            company = "Canva",
            title = "Backend Engineer",
            resumeId = Guid.NewGuid(),
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await WithDbAsync(async db => Assert.Equal(0, await db.JobApplications.CountAsync(Ct)));
    }

    [Fact]
    public async Task UpdateApplication_RefusesAnUnknownResumeId_AndAppliesNothingElseEither()
    {
        // The check has to run BEFORE any field is mutated, or a request that is
        // rejected still moves the ones it got to first. That is the part worth
        // pinning: the title below must be unchanged, not just the resume link.
        var id = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        var response = await Client.PatchAsJsonAsync($"/applications/{id}", new
        {
            title = "Staff Engineer",
            resumeId = Guid.NewGuid(),
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await WithDbAsync(async db =>
        {
            var posting = await db.JobApplications
                .Select(a => a.Posting.Title)
                .SingleAsync(Ct);
            Assert.Equal("Backend Engineer", posting);
        });
    }

    [Fact]
    public async Task CreateApplication_AcceptsAResumeIdThatExists()
    {
        // The check is worthless if it also refuses the real thing, and the real
        // resume comes from the import pipeline rather than a hand-built row.
        var client = AppWithModel(ResumeReply);
        var importId = await ImportAsync(client, "resume.pdf", DocumentKind.Resume, label: "mine");
        (await client.PostAsync($"/imports/{importId}/confirm", null, Ct)).EnsureSuccessStatusCode();

        var resumeId = await WithDbAsync(async db => (await db.Resumes.SingleAsync(Ct)).Id);

        var applicationId = await Client.CreateApplicationAsync(
            "Canva", "Backend Engineer", Ct, resumeId: resumeId);

        await WithDbAsync(async db =>
        {
            var application = await db.JobApplications.SingleAsync(a => a.Id == applicationId, Ct);
            Assert.Equal(resumeId, application.ResumeId);
        });
    }
}
