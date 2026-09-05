using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jobkeep.Contracts.Applications;
using Jobkeep.Contracts.Shared;
using Jobkeep.Modules.Applications;
using Jobkeep.Modules.Applications.Domain;
using Jobkeep.Modules.Documents;
using Jobkeep.Modules.Documents.Domain;
using Jobkeep.Tests.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Tests.Documents;

/// <summary>
/// Phase 4.5 — the upload → review → confirm cycle, end to end.
///
/// <para>
/// Everything is real except the model: real Postgres, real HTTP multipart
/// upload, real PdfPig and OpenXml parsing of real fixture files, real
/// migrations, real Program.cs. Only <see cref="FakeChatClient"/> stands in, for
/// the reason Phase 4 wrote down — a language model's output is
/// non-deterministic, so an assertion about it is either vacuous or flaky, while
/// everything on THIS side of the boundary is deterministic and is exactly where
/// the bugs are.
/// </para>
///
/// <para>
/// What these tests are really pinning is the gate: that an uploaded document
/// writes exactly one row nothing else reads, and that resumes, applications,
/// skills and requirements come into existence only when a human confirms.
/// </para>
/// </summary>
public class ImportTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    /// <summary>
    /// A client against the real app with the model swapped for a canned reply.
    /// WithWebHostBuilder rather than a hook on the shared factory: the
    /// registration added here lands after Program.cs's own, and last-registered
    /// wins for a single resolve.
    /// </summary>
    private HttpClient AppWithModel(string json) =>
        Fixture.App
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddSingleton<IChatClient>(new FakeChatClient(json))))
            .CreateClient().AsTestUser();

    // The model's proposal for the fixture resume. Two details are deliberate
    // and are asserted on below: "C#" appears TWICE (a real model does this, and
    // the composite key on resume_skills would turn it into a duplicate-key
    // exception without the dedup), and there is a padding experience entry with
    // an empty employer (a model filling a required array with nothing to put
    // in it).
    private const string ResumeReply = """
        {
          "fullName": "Jane Doe",
          "email": "jane.doe@example.com",
          "phone": "+61 400 000 000",
          "location": "Melbourne, Australia",
          "headline": "Backend engineer building payment services.",
          "skills": ["C#", "PostgreSQL", "C#", "Kubernetes"],
          "experience": [
            {
              "employer": "Canva",
              "title": "Senior Engineer",
              "start": "Mar 2021",
              "end": "Present",
              "highlights": ["Built payment reconciliation services in C#."]
            },
            { "employer": "", "title": "", "start": "", "end": "", "highlights": [] }
          ],
          "education": [
            {
              "institution": "University of Melbourne",
              "qualification": "Bachelor of Computer Science",
              "year": "2017"
            }
          ]
        }
        """;

    private const string PostingReply = """
        {
          "company": "Atlassian",
          "title": "Senior Backend Engineer",
          "location": "Melbourne, hybrid",
          "skills": [
            { "name": "C#", "required": true },
            { "name": "Kubernetes", "required": false }
          ],
          "requirements": [
            { "text": "Five years building backend services.", "kind": "qualification", "mustHave": true },
            { "text": "Mentor junior engineers.", "kind": "responsibility", "mustHave": false }
          ]
        }
        """;

    // A posting the model structured with no company name. Phase 13.2c: the
    // refusal comes back through IApplicationContract as a Refused result, which
    // is how CommitImport knows nothing was created and the claim can be rewound.
    private const string BlankCompanyReply = """
        {
          "company": "",
          "title": "Senior Backend Engineer",
          "location": null,
          "skills": [],
          "requirements": []
        }
        """;

    // Named FixtureBytes, not Fixture: IntegrationTestBase already exposes a
    // Fixture property (the Postgres container), and shadowing it here would
    // hide the thing every other test in the suite uses by that name.
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

    // The raw upload, and nothing after it. Since Phase 6.5 group 6 this leaves
    // the row in Parsing with no draft on it — use it only when the split itself
    // is what is under test.
    private async Task<JsonDocument> UploadOnlyAsync(
        HttpClient client, string fixtureName, DocumentKind kind, string? label = null)
    {
        var response = await client.PostAsync(
            "/imports", Upload(FixtureBytes(fixtureName), fixtureName, kind, label), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    // An import with a DRAFT on it, which is what most tests here mean by "an
    // import". Group 6 made that two requests instead of one: the upload saves
    // the text and returns, and the model runs in /reparse. The real client does
    // exactly this pair (Upload.tsx drives the second call from the review
    // screen), so this helper is the sequence under test, not a shortcut past it.
    private async Task<JsonDocument> PostImportAsync(
        HttpClient client, string fixtureName, DocumentKind kind, string? label = null)
    {
        using var uploaded = await UploadOnlyAsync(client, fixtureName, kind, label);
        var id = uploaded.RootElement.GetProperty("id").GetGuid();

        var parsed = await client.PostAsync($"/imports/{id}/reparse", null, Ct);
        parsed.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await parsed.Content.ReadAsStringAsync(Ct));
    }

    // ---------------------------------------------------------------- upload

    [Fact]
    public async Task Upload_ReturnsWithoutCallingTheModel_AndWritesNoRecordsAtAll()
    {
        // Phase 6.5 group 6. The upload used to block on the model for up to 180
        // seconds and hand back a finished draft; it now returns as soon as the
        // text is extracted and saved, leaving the row in Parsing for the client
        // to drive through /reparse. This test pins the half that got faster.
        var fake = new FakeChatClient(ResumeReply);
        var client = Fixture.App
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IChatClient>(fake)))
            .CreateClient().AsTestUser();

        using var body = await UploadOnlyAsync(client, "resume.pdf", DocumentKind.Resume);
        var root = body.RootElement;

        Assert.Equal("Parsing", root.GetProperty("status").GetString());
        Assert.Equal("Pdf", root.GetProperty("format").GetString());

        // THE THING THE PHASE BOUGHT: the upload did not talk to the model at
        // all. Everything it does is byte work, and byte work is fast.
        Assert.Null(fake.LastPrompt);
        Assert.True(root.GetProperty("modelUsed").ValueKind == JsonValueKind.Null);

        // The extracted text is returned, because the review screen's job is to
        // let a human compare the draft against the document — and since the
        // draft arrives later, the text is the only thing that screen has to
        // show while the model runs.
        Assert.Contains("University of Melbourne", root.GetProperty("extractedText").GetString());

        // THE POINT OF THE PHASE: an upload has written one document_imports row
        // and nothing else. No resume, no skills, no application.
        await WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.DocumentImports.CountAsync(Ct));
            Assert.Equal(0, await db.Resumes.CountAsync(Ct));
            Assert.Equal(0, await db.Skills.CountAsync(Ct));
            Assert.Equal(0, await db.JobApplications.CountAsync(Ct));
        });
    }

    [Fact]
    public async Task Reparse_FinishesTheUpload_ProducingTheDraftAndLeavingParsing()
    {
        // The other half of the split: the model runs in the second request, and
        // the row leaves Parsing when it succeeds. The draft assertions live here
        // now because this is where the draft is first produced.
        var client = AppWithModel(ResumeReply);
        using var created = await UploadOnlyAsync(client, "resume.pdf", DocumentKind.Resume);
        var id = created.RootElement.GetProperty("id").GetGuid();

        var response = await client.PostAsync($"/imports/{id}/reparse", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = body.RootElement;

        // Parsing is closed by the parse itself, not by a sweeper.
        Assert.Equal("AwaitingReview", root.GetProperty("status").GetString());

        var resume = root.GetProperty("draft").GetProperty("resume");
        Assert.Equal("Jane Doe", resume.GetProperty("fullName").GetString());

        // "C#" was proposed twice; it survives once.
        var skills = resume.GetProperty("skills").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Equal(["C#", "PostgreSQL", "Kubernetes"], skills);

        // The padding entry with no employer is dropped rather than shown to the
        // user as a blank card to fix.
        Assert.Single(resume.GetProperty("experience").EnumerateArray());

        // Still nothing but the import row. The gate is unmoved by the split.
        await WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.DocumentImports.CountAsync(Ct));
            Assert.Equal(0, await db.Resumes.CountAsync(Ct));
        });
    }

    [Fact]
    public async Task Reparse_IsRefused_ForAnImportThatIsStillParsing_OnlyWhenItIsNotParsing()
    {
        // The guard that had to change. /reparse used to demand AwaitingReview,
        // which would have refused every row the new upload creates — the plan
        // for this group assumed the endpoint already accepted Parsing, and it
        // did not. Confirm and edit still refuse a Parsing row, because the
        // draft it would act on does not exist yet.
        var client = AppWithModel(ResumeReply);
        using var created = await UploadOnlyAsync(client, "resume.pdf", DocumentKind.Resume);
        var id = created.RootElement.GetProperty("id").GetGuid();

        var confirm = await client.PostAsync($"/imports/{id}/confirm", null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Contains("still being read", await confirm.Content.ReadAsStringAsync(Ct));

        // And the refusal says "not yet", never "already" or "no longer" — the
        // wording every other refusal here uses would be a lie about a state
        // that clears itself.
        Assert.DoesNotContain("already", await confirm.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Upload_ReadsADocxIncludingTextInsideTables()
    {
        var client = AppWithModel(ResumeReply);

        using var body = await PostImportAsync(client, "resume.docx", DocumentKind.Resume);

        Assert.Equal("Docx", body.RootElement.GetProperty("format").GetString());
        Assert.Contains("Kubernetes", body.RootElement.GetProperty("extractedText").GetString());
    }

    [Fact]
    public async Task Upload_RefusesALegacyDoc_WithAMessageSayingWhatToDo()
    {
        var client = AppWithModel(ResumeReply);

        var response = await client.PostAsync(
            "/imports", Upload(FixtureBytes("legacy.doc"), "resume.doc", DocumentKind.Resume), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(".docx", await response.Content.ReadAsStringAsync(Ct));

        // A refused upload leaves nothing behind.
        await WithDbAsync(async db => Assert.Equal(0, await db.DocumentImports.CountAsync(Ct)));
    }

    [Fact]
    public async Task Upload_KeepsTheExtraction_WhenTheModelReturnsSomethingUnusable()
    {
        // The failure this design exists to survive. A successful PDF parse must
        // not be thrown away because the model was down or answered rubbish —
        // the text is saved first, so /reparse can retry the half that failed
        // without the user finding the file again.
        //
        // Group 6 moved WHERE this failure happens, not what it costs: the model
        // is called by /reparse now, so the bad answer arrives there. The
        // user-visible outcome is deliberately unchanged — the row lands in
        // AwaitingReview with the extraction intact and a warning attached,
        // exactly as POST /imports used to leave it.
        var client = AppWithModel("{ this is not json");

        using var created = await UploadOnlyAsync(client, "resume.pdf", DocumentKind.Resume);
        var id = created.RootElement.GetProperty("id").GetGuid();

        var response = await client.PostAsync($"/imports/{id}/reparse", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = body.RootElement;

        // NOT left in Parsing. A row that claims a parse nobody is running is
        // the invisible-orphan state this status exists to remove, so a failure
        // while finishing an upload has to close it out.
        Assert.Equal("AwaitingReview", root.GetProperty("status").GetString());
        Assert.Contains("Jane Doe", root.GetProperty("extractedText").GetString());
        Assert.False(root.GetProperty("warning").ValueKind == JsonValueKind.Null);

        await WithDbAsync(async db =>
        {
            var import = await db.DocumentImports.SingleAsync(Ct);
            Assert.Equal(ImportStatus.AwaitingReview, import.Status);
            Assert.NotEmpty(import.ExtractedText);
            Assert.Null(import.ModelUsed);
        });
    }

    [Fact]
    public async Task Reparse_AskedForByAHuman_ReportsAModelFailureRatherThanSwallowingIt()
    {
        // The other side of that branch, and the reason it is a branch at all.
        // Finishing an upload must not leave the row stuck, so a failure there
        // becomes a warning. A re-parse a human PRESSED is a question, so a
        // failure is the answer to it — Invalid, and the existing draft is left
        // exactly as it was rather than being replaced by a worse one.
        var good = AppWithModel(ResumeReply);
        using var created = await PostImportAsync(good, "resume.pdf", DocumentKind.Resume);
        var id = created.RootElement.GetProperty("id").GetGuid();

        var bad = AppWithModel("{ not json at all");
        var response = await bad.PostAsync($"/imports/{id}/reparse", null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var after = await good.GetAsync($"/imports/{id}", Ct);
        using var body = JsonDocument.Parse(await after.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Jane Doe",
            body.RootElement.GetProperty("draft").GetProperty("resume")
                .GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task Upload_DoesNotCallTheModel_ForAScannedPdfWithNoTextLayer()
    {
        // Asking a model to structure an empty string does not fail — it invents
        // a plausible resume, which is the worst thing this feature could do.
        var fake = new FakeChatClient(ResumeReply);
        var client = Fixture.App
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IChatClient>(fake)))
            .CreateClient().AsTestUser();

        var response = await client.PostAsync(
            "/imports", Upload(FixtureBytes("scanned.pdf"), "scanned.pdf", DocumentKind.Resume), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.Null(fake.LastPrompt);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Contains("scan", body.RootElement.GetProperty("warning").GetString()!,
            StringComparison.OrdinalIgnoreCase);

        // AwaitingReview, NOT Parsing. This import is finished the moment it is
        // saved: there is no text to send, so nothing will ever be driven for
        // it, and marking it Parsing would strand it in a state whose only exit
        // is a parse that cannot happen. It is the one upload path that still
        // completes in a single request.
        Assert.Equal("AwaitingReview", body.RootElement.GetProperty("status").GetString());
    }

    // ---------------------------------------------------------------- review

    [Fact]
    public async Task Review_ReplacesTheDraft_AndStopsClaimingTheModelWroteIt()
    {
        var client = AppWithModel(ResumeReply);
        using var created = await PostImportAsync(client, "resume.pdf", DocumentKind.Resume);
        var id = created.RootElement.GetProperty("id").GetGuid();

        // The model got the employer wrong. This is the user fixing it — the
        // whole reason the pipeline stops at a draft.
        var corrected = new
        {
            resume = new
            {
                label = "backend-focused",
                fullName = "Jane Doe",
                email = "jane@example.com",
                phone = (string?)null,
                location = "Melbourne",
                headline = (string?)null,
                skills = new[] { "C#", "Rust" },
                experience = new[]
                {
                    new
                    {
                        employer = "Canva Pty Ltd",
                        title = "Senior Engineer",
                        start = "Mar 2021",
                        end = "Present",
                        highlights = new[] { "Payments." }
                    }
                },
                education = Array.Empty<object>()
            },
            posting = (object?)null
        };

        var response = await client.PutAsJsonAsync($"/imports/{id}", corrected, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await WithDbAsync(async db =>
        {
            var import = await db.DocumentImports.SingleAsync(Ct);
            Assert.Contains("Canva Pty Ltd", import.DraftJson);
            Assert.Contains("Rust", import.DraftJson);
            // A provenance column that keeps naming the model after a human
            // rewrote the content is worse than not having one.
            Assert.Null(import.ModelUsed);
        });
    }

    [Fact]
    public async Task Review_RefusesADraftOfTheWrongKind()
    {
        var client = AppWithModel(ResumeReply);
        using var created = await PostImportAsync(client, "resume.pdf", DocumentKind.Resume);
        var id = created.RootElement.GetProperty("id").GetGuid();

        var wrong = new
        {
            resume = (object?)null,
            posting = new
            {
                company = "Canva",
                title = "Engineer",
                location = (string?)null,
                description = (string?)null,
                sourceUrl = (string?)null,
                skills = Array.Empty<object>(),
                requirements = Array.Empty<object>()
            }
        };

        var response = await client.PutAsJsonAsync($"/imports/{id}", wrong, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reparse_RebuildsTheDraftFromStoredText_WithNoReUpload()
    {
        // The dividend of storing the extracted text between the two stages: a
        // better prompt or a better model costs no re-upload.
        var client = AppWithModel("{ not json either");
        using var created = await UploadOnlyAsync(client, "resume.pdf", DocumentKind.Resume);
        var id = created.RootElement.GetProperty("id").GetGuid();

        var better = AppWithModel(ResumeReply);
        var response = await better.PostAsync($"/imports/{id}/reparse", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Jane Doe",
            body.RootElement.GetProperty("draft").GetProperty("resume")
                .GetProperty("fullName").GetString());
    }

    // --------------------------------------------------------------- confirm

    [Fact]
    public async Task Confirm_CreatesTheResumeWithItsSkillsExperienceAndEducation()
    {
        var client = AppWithModel(ResumeReply);
        using var created = await PostImportAsync(
            client, "resume.pdf", DocumentKind.Resume, label: "backend-focused");
        var id = created.RootElement.GetProperty("id").GetGuid();

        var response = await client.PostAsync($"/imports/{id}/confirm", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await WithDbAsync(async db =>
        {
            var resume = await db.Resumes
                .Include(r => r.Experiences)
                .Include(r => r.Educations)
                .Include(r => r.ResumeSkills)
                .SingleAsync(Ct);

            Assert.Equal("backend-focused", resume.Label);
            Assert.Equal("Jane Doe", resume.FullName);
            // The verbatim extracted text, not the draft — Phase 5's match check
            // compares against the document, not against the model's summary.
            Assert.Contains("University of Melbourne", resume.SourceText);

            var experience = Assert.Single(resume.Experiences);
            Assert.Equal("Canva", experience.Employer);
            // Dates stay as the document wrote them. No DateOnly, no guessing.
            Assert.Equal("Mar 2021", experience.StartText);
            Assert.Equal("Present", experience.EndText);
            Assert.Single(experience.Highlights);

            var education = Assert.Single(resume.Educations);
            Assert.Equal("University of Melbourne", education.Institution);

            Assert.Equal(3, resume.ResumeSkills.Count);
            Assert.All(resume.ResumeSkills, s => Assert.Equal(SkillSource.AiExtracted, s.Source));

            var import = await db.DocumentImports.SingleAsync(Ct);
            Assert.Equal(ImportStatus.Committed, import.Status);
            Assert.Equal(resume.Id, import.CommittedEntityId);
        });
    }

    [Fact]
    public async Task Confirm_IsRefusedTheSecondTime()
    {
        // Makes the confirm button safe to double-click, which is not
        // hypothetical on a request that has just taken several seconds.
        var client = AppWithModel(ResumeReply);
        using var created = await PostImportAsync(
            client, "resume.pdf", DocumentKind.Resume, label: "one");
        var id = created.RootElement.GetProperty("id").GetGuid();

        (await client.PostAsync($"/imports/{id}/confirm", null, Ct)).EnsureSuccessStatusCode();
        var second = await client.PostAsync($"/imports/{id}/confirm", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        await WithDbAsync(async db => Assert.Equal(1, await db.Resumes.CountAsync(Ct)));
    }

    [Fact]
    public async Task Confirm_RefusesADuplicateLabel_WithASentenceRatherThanAnIndexViolation()
    {
        var client = AppWithModel(ResumeReply);

        using var first = await PostImportAsync(
            client, "resume.pdf", DocumentKind.Resume, label: "generalist");
        (await client.PostAsync(
            $"/imports/{first.RootElement.GetProperty("id").GetGuid()}/confirm", null, Ct))
            .EnsureSuccessStatusCode();

        using var second = await PostImportAsync(
            client, "resume.docx", DocumentKind.Resume, label: "generalist");
        var response = await client.PostAsync(
            $"/imports/{second.RootElement.GetProperty("id").GetGuid()}/confirm", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("already exists", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Confirm_ForAJobPosting_CreatesAnApplicationWithItsSkillsAndRequirements()
    {
        var client = AppWithModel(PostingReply);
        using var created = await PostImportAsync(client, "resume.txt", DocumentKind.JobPosting);
        var id = created.RootElement.GetProperty("id").GetGuid();

        var response = await client.PostAsync($"/imports/{id}/confirm", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await WithDbAsync(async db =>
        {
            var names = await SkillNamesAsync(db);
            var application = await db.JobApplications
                .Include(a => a.Posting).ThenInclude(p => p.Company)
                .Include(a => a.Posting).ThenInclude(p => p.PostingSkills)
                .Include(a => a.Posting).ThenInclude(p => p.Requirements)
                .SingleAsync(Ct);

            // Created through Applications' own use case, so its company dedup
            // and its validation ran — there is one implementation of "create an
            // application", not two.
            Assert.Equal("Atlassian", application.Posting.Company.Name);
            Assert.Equal("Senior Backend Engineer", application.Posting.Title);

            Assert.Equal(2, application.Posting.PostingSkills.Count);
            Assert.True(application.Posting.PostingSkills.Single(s => names[s.SkillId] == "C#").IsRequired);

            Assert.Equal(2, application.Posting.Requirements.Count);
            Assert.Contains(application.Posting.Requirements,
                r => r.Kind == RequirementKind.Qualification && r.IsMustHave);
        });
    }

    /// <summary>
    /// Phase 13.2c. Confirming a job ad stopped being one database transaction —
    /// the application is created by another module, through
    /// <c>IApplicationContract</c>, and at 13.3 that is a different service with no
    /// transaction to join. What replaced the transaction is a three-step protocol,
    /// and this is the step that carries the risk.
    ///
    /// <para>
    /// The failure the transaction protected against was never a lost write. It was
    /// a DUPLICATE one: the application committed, something after it failed, the
    /// import still read <c>AwaitingReview</c>, and confirming again logged the same
    /// job twice. The replacement is <c>CommittedEntityId</c>, written the moment
    /// the application exists — so a re-run finds it and only closes the import out.
    /// </para>
    ///
    /// <para>
    /// Seeded rather than provoked. Making the real commit fail half-way needs a
    /// fault injected into another module's SaveChanges, which would test the
    /// injection more than the protocol; the state a crash leaves behind is a row,
    /// and a row can simply be written. What is asserted is the thing that matters:
    /// re-confirming that row creates no second application.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Confirm_AfterAHalfFinishedCommit_ClosesTheImportOut_AndDoesNotLogTheJobTwice()
    {
        var client = AppWithModel(PostingReply);
        using var created = await PostImportAsync(client, "resume.txt", DocumentKind.JobPosting);
        var id = created.RootElement.GetProperty("id").GetGuid();

        // A first confirm that got as far as creating the application and no further.
        (await client.PostAsync($"/imports/{id}/confirm", null, Ct)).EnsureSuccessStatusCode();
        var applicationId = await WithDbAsync(async db =>
        {
            var application = await db.JobApplications.SingleAsync(Ct);
            var import = await db.DocumentImports.SingleAsync(d => d.Id == id, Ct);
            import.Status = ImportStatus.CommitFailed;
            import.CommittedAtUtc = null;
            await db.SaveChangesAsync(Ct);
            return application.Id;
        });

        // The user presses confirm again, which is what CommitFailed tells them to do.
        var second = await client.PostAsync($"/imports/{id}/confirm", null, Ct);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await WithDbAsync(async db =>
        {
            // The whole point. One application, not two.
            Assert.Equal(1, await db.JobApplications.CountAsync(Ct));
            Assert.Equal(1, await db.JobPostings.CountAsync(Ct));

            var import = await db.DocumentImports.SingleAsync(d => d.Id == id, Ct);
            Assert.Equal(ImportStatus.Committed, import.Status);
            Assert.Equal(applicationId, import.CommittedEntityId);
            Assert.NotNull(import.CommittedAtUtc);
        });
    }

    /// <summary>
    /// The other half of the protocol: a refusal is a clean no-op, so the claim the
    /// commit put on the import is REWOUND rather than left as CommitFailed. The
    /// user's draft is still editable, which is the state they need to be in.
    /// </summary>
    [Fact]
    public async Task Confirm_WhenApplicationsRefusesTheDraft_LeavesTheImportEditable()
    {
        // A posting draft with no company. CreateApplicationHandler refuses it, and
        // this file deliberately does not duplicate that check.
        var client = AppWithModel(BlankCompanyReply);
        using var created = await PostImportAsync(client, "resume.txt", DocumentKind.JobPosting);
        var id = created.RootElement.GetProperty("id").GetGuid();

        var response = await client.PostAsync($"/imports/{id}/confirm", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await WithDbAsync(async db =>
        {
            Assert.Equal(0, await db.JobApplications.CountAsync(Ct));

            var import = await db.DocumentImports.SingleAsync(d => d.Id == id, Ct);
            Assert.Equal(ImportStatus.AwaitingReview, import.Status);
            Assert.Null(import.CommittedAtUtc);
            Assert.Null(import.CommittedEntityId);
        });
    }

    [Fact]
    public async Task ResumeSkillsAndPostingSkills_ShareTheSameSkillRow()
    {
        // The payoff, and the reason `skills` was made a shared table in Phase 2.
        // Once a resume's "C#" IS a posting's "C#", the question Phase 5 asks —
        // what do these jobs want that my resume never mentions — is a join
        // rather than a string comparison across two vocabularies.
        var posting = AppWithModel(PostingReply);
        using var postingImport = await PostImportAsync(posting, "resume.txt", DocumentKind.JobPosting);
        (await posting.PostAsync(
            $"/imports/{postingImport.RootElement.GetProperty("id").GetGuid()}/confirm", null, Ct))
            .EnsureSuccessStatusCode();

        var resume = AppWithModel(ResumeReply);
        using var resumeImport = await PostImportAsync(
            resume, "resume.pdf", DocumentKind.Resume, label: "mine");
        (await resume.PostAsync(
            $"/imports/{resumeImport.RootElement.GetProperty("id").GetGuid()}/confirm", null, Ct))
            .EnsureSuccessStatusCode();

        await WithDbAsync(async db =>
        {
            var csharp = await db.Skills.SingleAsync(s => s.Name == "C#", Ct);

            Assert.True(await db.PostingSkills.AnyAsync(ps => ps.SkillId == csharp.Id, Ct));
            Assert.True(await db.ResumeSkills.AnyAsync(rs => rs.SkillId == csharp.Id, Ct));

            // Three from the resume + Kubernetes and C# from the posting, with
            // C# and Kubernetes shared: C#, PostgreSQL, Kubernetes = 3 rows.
            Assert.Equal(3, await db.Skills.CountAsync(Ct));
        });
    }

    // --------------------------------------------------------- discard + list

    [Fact]
    public async Task Discard_MarksTheImport_KeepsItsTextAndBlocksConfirm()
    {
        var client = AppWithModel(ResumeReply);
        using var created = await PostImportAsync(client, "resume.pdf", DocumentKind.Resume);
        var id = created.RootElement.GetProperty("id").GetGuid();

        var deleted = await client.DeleteAsync($"/imports/{id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var confirm = await client.PostAsync($"/imports/{id}/confirm", null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);

        await WithDbAsync(async db =>
        {
            var import = await db.DocumentImports.SingleAsync(Ct);
            Assert.Equal(ImportStatus.Discarded, import.Status);
            // Kept, not deleted: the text is what tells you whether a bad import
            // was the PDF's fault or the model's.
            Assert.NotEmpty(import.ExtractedText);
        });
    }

    [Fact]
    public async Task List_ShowsTheReviewQueue_AndNeverTheDocumentText()
    {
        var client = AppWithModel(ResumeReply);
        using var kept = await PostImportAsync(client, "resume.pdf", DocumentKind.Resume);
        using var dropped = await PostImportAsync(client, "resume.docx", DocumentKind.Resume);
        (await client.DeleteAsync($"/imports/{dropped.RootElement.GetProperty("id").GetGuid()}", Ct))
            .EnsureSuccessStatusCode();

        var response = await client.GetAsync("/imports", Ct);
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var item = Assert.Single(body.RootElement.EnumerateArray());

        Assert.Equal(kept.RootElement.GetProperty("id").GetGuid(), item.GetProperty("id").GetGuid());

        // A résumé is personal data and a list endpoint is the wrong place to
        // spray it. Same finding, same fix, as the Phase 2.3 list projection.
        Assert.False(item.TryGetProperty("extractedText", out _));
        Assert.False(item.TryGetProperty("draft", out _));
        Assert.True(item.GetProperty("textLength").GetInt32() > 0);
    }

    [Fact]
    public async Task GraphQL_AndRest_ReadTheSameImport()
    {
        // Surface parity: the upload is REST-only by design, but everything after
        // the bytes arrive is on both surfaces and goes through one handler.
        var client = AppWithModel(ResumeReply);
        using var created = await PostImportAsync(client, "resume.pdf", DocumentKind.Resume);
        var id = created.RootElement.GetProperty("id").GetGuid();

        // A GraphQL client over THIS http client, not the base class's: the model
        // fake is registered on this one, and the inherited GraphQL helper talks
        // to the default app where IChatClient is still the real Ollama.
        var graphql = new GraphQLClient(client);

        var result = await graphql.QueryAsync(
            "query($id: UUID!) { import(id: $id) { status draft { resume { fullName skills } } } }",
            new { id });

        Assert.False(result.HasErrors, result.FirstErrorMessage);
        var import = result.Data!.Value.GetProperty("import");
        Assert.Equal("AWAITING_REVIEW", import.GetProperty("status").GetString());
        Assert.Equal("Jane Doe",
            import.GetProperty("draft").GetProperty("resume").GetProperty("fullName").GetString());
    }
}
