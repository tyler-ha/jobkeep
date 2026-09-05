using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Jobkeep.Modules.Documents.Domain;
using Jobkeep.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Tests.Documents;

/// <summary>
/// Phase 6.5 group 6 — the background worker that structures an uploaded document
/// after the upload request has already returned.
///
/// <para>
/// This is the ONLY test in the suite that runs with
/// <c>Documents:ParseInBackground</c> on. Everywhere else it is off, because a
/// worker sweeping Parsing rows races Respawn's truncation and would structure
/// documents out from under unrelated arranges — see JobkeepAppFactory. The other
/// import tests therefore call <c>POST /imports/{id}/reparse</c> themselves, which
/// is exactly what the worker calls, so the SLICE is covered thirty times over and
/// only the TRIGGER is not.
/// </para>
///
/// <para>
/// The trigger is the whole point of the group, though. An earlier version of this
/// feature had the browser drive the parse, and it was replaced precisely because
/// closing the tab stranded the row — so "does the server finish the job on its
/// own" is the property that had to become testable, and a background mechanism
/// nobody exercises is a background mechanism that has never run.
/// </para>
/// </summary>
public class ImportParseWorkerTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string ResumeReply = """
        {
          "fullName": "Jane Doe",
          "email": "jane.doe@example.com",
          "phone": null,
          "location": "Melbourne, Australia",
          "headline": "Backend engineer.",
          "skills": ["C#"],
          "experience": [],
          "education": []
        }
        """;

    // The app with the worker switched back ON. UseSetting rather than a service
    // override because AddDocumentsModule reads the flag off configuration when it
    // decides whether to call AddHostedService at all — by the time the container
    // exists, the decision has been made.
    private HttpClient AppWithWorker(string modelReply) =>
        Fixture.App
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Documents:ParseInBackground", "true");
                b.ConfigureServices(s => s.AddSingleton<IChatClient>(new FakeChatClient(modelReply)));
            })
            .CreateClient().AsTestUser();

    private static byte[] FixtureBytes(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static MultipartFormDataContent Upload(byte[] bytes, string fileName) =>
        new()
        {
            { new ByteArrayContent(bytes), "file", fileName },
            { new StringContent(nameof(DocumentKind.Resume)), "kind" }
        };

    /// <summary>
    /// Polls until the import leaves Parsing, or gives up. Bounded so a broken
    /// worker FAILS rather than hanging the suite — an unbounded wait on a
    /// background thread is how a test run becomes a mystery.
    /// </summary>
    private async Task<ImportStatus> WaitForParseAsync(Guid id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = ImportStatus.Parsing;
            await WithDbAsync(async db =>
                status = await db.DocumentImports.Where(d => d.Id == id)
                    .Select(d => d.Status).SingleAsync(Ct));

            if (status != ImportStatus.Parsing) return status;
            await Task.Delay(100, Ct);
        }

        return ImportStatus.Parsing;
    }

    [Fact]
    public async Task TheWorker_FinishesAnUpload_WithNobodyDrivingIt()
    {
        // THE PROPERTY THE WHOLE REVERSAL BUYS. Nothing here calls /reparse. The
        // upload returns, the client does nothing further — as if the tab had been
        // closed the instant the POST came back — and the draft appears anyway.
        var client = AppWithWorker(ResumeReply);

        var response = await client.PostAsync(
            "/imports", Upload(FixtureBytes("resume.pdf"), "resume.pdf"), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var id = body.RootElement.GetProperty("id").GetGuid();

        // The upload itself still hands back a Parsing row: the worker is a
        // consequence of the save, not part of the request.
        Assert.Equal("Parsing", body.RootElement.GetProperty("status").GetString());

        var status = await WaitForParseAsync(id, TimeSpan.FromSeconds(30));
        Assert.Equal(ImportStatus.AwaitingReview, status);

        await WithDbAsync(async db =>
        {
            var import = await db.DocumentImports.SingleAsync(d => d.Id == id, Ct);

            // Structured by the worker, not merely marked done — ModelUsed is only
            // written on the success path of RestructureImport.
            Assert.NotNull(import.ModelUsed);
            Assert.Contains("Jane Doe", import.DraftJson);

            // And still nothing but the import row. The confirm gate is unmoved by
            // any of this: a worker parses a draft, it does not commit one.
            Assert.Equal(0, await db.Resumes.CountAsync(Ct));
        });
    }

    [Fact]
    public async Task TheWorker_RecoversARowLeftParsingByAPreviousRun()
    {
        // The startup sweep, which is the reason the durable queue is a COLUMN and
        // not the Channel<Guid>. A channel does not survive a restart; Status does.
        //
        // The arrange is a row stranded exactly as a crash would strand one —
        // written straight to the database, so no channel message ever existed for
        // it — and the assert is that a host booting afterwards picks it up with no
        // request involved at all.
        var id = Guid.NewGuid();
        await WithDbAsync(async db =>
        {
            db.DocumentImports.Add(new DocumentImport
            {
                Id = id,
                Kind = DocumentKind.Resume,
                Status = ImportStatus.Parsing,
                FileName = "stranded.pdf",
                Format = SourceFormat.Pdf,
                ByteCount = 1234,
                ContentHash = new string('a', 64),
                ExtractedText = new string('x', 500),
                DraftJson = """{"resume":{"label":"stranded"},"posting":null}"""
            });
            await db.SaveChangesAsync(Ct);
        });

        // Creating the client is what boots the host, and booting is what sweeps.
        var client = AppWithWorker(ResumeReply);
        (await client.GetAsync("/imports", Ct)).EnsureSuccessStatusCode();

        var status = await WaitForParseAsync(id, TimeSpan.FromSeconds(30));
        Assert.Equal(ImportStatus.AwaitingReview, status);
    }
}
