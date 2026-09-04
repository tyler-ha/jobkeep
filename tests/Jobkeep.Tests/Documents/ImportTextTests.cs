using System.Net;
using System.Text;
using System.Text.Json;
using Jobkeep.Modules.Documents.Domain;
using Jobkeep.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Tests.Documents;

/// <summary>
/// Phase 6.5 group 4 — pasting an advertisement instead of uploading a file.
///
/// <para>
/// The claim this file has to make good is narrow and strong: <b>a paste and an
/// uploaded .txt of the same words are the same import.</b> Not "similar", not
/// "both work" — byte-identical content hash, character-identical extracted
/// text, same format, same status. That is what makes the paste route a second
/// door onto one pipeline rather than a second pipeline, and it is the only
/// property that stops the two drifting apart later.
/// </para>
///
/// <para>
/// The model is faked here for the reason the rest of the suite fakes it, and
/// it matters more than usual: the interesting question about a paste is
/// whether the WORDS survive the trip, and that is decided entirely by the
/// deterministic half. If a keyword reaches <c>extractedText</c> intact then
/// the model saw it; whether the model then did anything sensible with it is
/// not a question a test can ask.
/// </para>
/// </summary>
public class ImportTextTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private HttpClient AppWithModel(string json) =>
        Fixture.App
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddSingleton<IChatClient>(new FakeChatClient(json))))
            .CreateClient();

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
            { "text": "Five years building backend services.", "kind": "qualification", "mustHave": true }
          ]
        }
        """;

    // ---------------------------------------------------------------------
    // The advertisement, and why it is shaped like this
    // ---------------------------------------------------------------------
    // Every awkward thing a real ctrl+A / ctrl+V off a job board drags along
    // with it, in one document: CRLF line endings from a Windows browser, a run
    // of blank lines where the page had spacing, unicode bullets and an em
    // dash, a non-breaking space inside a phrase, tab-separated pseudo-columns
    // out of a benefits table, trailing spaces on several lines, and accented
    // characters.
    //
    // KEYWORDS is the assertion. Each entry sits behind a different one of
    // those hazards, so a keyword that goes missing names the hazard that ate
    // it: "PostgreSQL" is behind a unicode bullet, "Kubernetes" behind a tab,
    // "CI/CD" behind a non-breaking space, "Montréal" behind non-ASCII, and
    // "Terraform" behind the blank-line run that Normalise collapses.
    private const string PastedAd =
        "Senior Backend Engineer  \r\n" +
        "Atlassian — Melbourne, hybrid   \r\n" +
        "\r\n" +
        "\r\n" +
        "\r\n" +
        "What you will do\r\n" +
        "• Build services in C# on .NET 10\r\n" +
        "• Own our PostgreSQL schema end to end\r\n" +
        "• Ship through a CI/CD pipeline you help design\r\n" +
        "\r\n" +
        "Infrastructure\tKubernetes\tAWS\r\n" +
        "Provisioning\tTerraform\t\r\n" +
        "\r\n" +
        "Our Montréal team keeps the same stack.\r\n" +
        "Five years building backend services.\r\n";

    private static readonly string[] Keywords =
    [
        "Senior Backend Engineer",
        "Atlassian",
        "Melbourne",
        "C#",
        ".NET 10",
        "PostgreSQL",
        "CI/CD",
        "Kubernetes",
        "AWS",
        "Terraform",
        "Montréal",
        "Five years building backend services.",
    ];

    private static StringContent Paste(string text, DocumentKind kind = DocumentKind.JobPosting) =>
        new(JsonSerializer.Serialize(new { text, kind = kind.ToString() }),
            Encoding.UTF8, "application/json");

    private async Task<JsonDocument> PasteAsync(HttpClient client, string text)
    {
        var response = await client.PostAsync("/imports/text", Paste(text), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    // ------------------------------------------------------------- the words

    [Fact]
    public async Task Paste_CarriesEveryKeywordThroughToTheExtractedText()
    {
        var client = AppWithModel(PostingReply);

        using var created = await PasteAsync(client, PastedAd);
        var text = created.RootElement.GetProperty("extractedText").GetString()!;

        // The whole point of the feature. Asserted one keyword at a time rather
        // than against a golden string, because a golden string fails as one
        // opaque diff while this fails naming the word that went missing.
        foreach (var keyword in Keywords)
            Assert.Contains(keyword, text);

        // And what the paste IS allowed to have changed: CRLF is gone, the
        // three-blank-line run has collapsed to one, and the trailing spaces on
        // the first two lines are trimmed. Normalise does this to every
        // document; it is asserted here because a paste is the only source that
        // routinely carries all three at once.
        Assert.DoesNotContain("\r", text);
        Assert.DoesNotContain("\n\n\n", text);
        Assert.DoesNotContain(" \n", text);

        // The keywords survive into the COLUMN too, not just the response: the
        // model reads that column, and so does every later /reparse.
        await WithDbAsync(async db =>
        {
            var stored = await db.DocumentImports.SingleAsync(Ct);
            foreach (var keyword in Keywords)
                Assert.Contains(keyword, stored.ExtractedText);
        });
    }

    [Fact]
    public async Task Paste_AndAnIdenticalTxtUpload_AreTheSameImport()
    {
        // THE STRONGEST TEST IN THIS FILE. Two doors, one pipeline: if these
        // ever disagree, the paste route has grown a code path of its own.
        var client = AppWithModel(PostingReply);

        using var pasted = await PasteAsync(client, PastedAd);

        // .Trim() because ImportTextHandler trims the paste before hashing it,
        // deliberately — a browser selection picks up whitespace that belongs to
        // the drag, not to the ad. Same bytes in, same hash out.
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes(PastedAd.Trim())), "file", "ad.txt" },
            { new StringContent(nameof(DocumentKind.JobPosting)), "kind" },
        };
        var response = await client.PostAsync("/imports", form, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var uploaded = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        foreach (var field in new[] { "contentHash", "extractedText", "format", "status" })
            Assert.Equal(
                uploaded.RootElement.GetProperty(field).GetString(),
                pasted.RootElement.GetProperty(field).GetString());

        Assert.Equal(
            uploaded.RootElement.GetProperty("byteCount").GetInt64(),
            pasted.RootElement.GetProperty("byteCount").GetInt64());

        // Only the display label differs, and it has to: there was no file.
        Assert.Equal("Pasted text", pasted.RootElement.GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task Paste_WritesOneRowAndNothingElse_ThenParsesLikeAnUpload()
    {
        // The same gate as the upload: a paste proposes, it does not create.
        var fake = new FakeChatClient(PostingReply);
        var client = Fixture.App
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IChatClient>(fake)))
            .CreateClient();

        using var created = await PasteAsync(client, PastedAd);
        var id = created.RootElement.GetProperty("id").GetGuid();

        // Group 6 applies to this route as much as to the upload, and for free:
        // the paste never touches the model either.
        Assert.Equal("Parsing", created.RootElement.GetProperty("status").GetString());
        Assert.Null(fake.LastPrompt);

        await WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.DocumentImports.CountAsync(Ct));
            Assert.Equal(0, await db.JobApplications.CountAsync(Ct));
            Assert.Equal(0, await db.Skills.CountAsync(Ct));
        });

        // The parse the background worker would have run, driven explicitly
        // because the worker is off under test (DocumentOptions.ParseInBackground).
        var parsed = await client.PostAsync($"/imports/{id}/reparse", null, Ct);
        parsed.EnsureSuccessStatusCode();
        using var draft = JsonDocument.Parse(await parsed.Content.ReadAsStringAsync(Ct));

        Assert.Equal("AwaitingReview", draft.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "Atlassian",
            draft.RootElement.GetProperty("draft").GetProperty("posting").GetProperty("company").GetString());

        // The model was shown the pasted words, not a paraphrase of them.
        Assert.Contains("Terraform", fake.LastPrompt);
    }

    // -------------------------------------------------------------- refusals

    [Theory]
    [InlineData("Backend dev wanted")]
    [InlineData("   ")]
    [InlineData("")]
    public async Task Paste_TooShort_IsRefusedWithASentenceSayingWhy(string text)
    {
        var response = await Client.PostAsync("/imports/text", Paste(text), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);

        // The refusal names the threshold, because "too short" without a number
        // leaves the user guessing how much more to paste. This is the ONE rule
        // the file path does not share: a scan with no text layer is saved and
        // warned about, a twelve-character paste is a slip.
        Assert.Contains("40", body);
        Assert.Contains("characters", body);

        await WithDbAsync(async db => Assert.Equal(0, await db.DocumentImports.CountAsync(Ct)));
    }

    [Fact]
    public async Task Paste_RefusalIsTheSameOnBothSurfaces()
    {
        var result = await GraphQL.QueryAsync(
            """
            mutation($text: String!) {
              importText(text: $text, kind: JOB_POSTING) { id }
            }
            """,
            new { text = "Backend dev wanted" });

        Assert.Equal("INVALID_INPUT", result.FirstErrorCode);
        Assert.Contains("40", result.FirstErrorMessage);
    }

    [Fact]
    public async Task Paste_WorksThroughGraphQLToo()
    {
        // The upload's REST-only exception covers RECEIVING BYTES and nothing
        // else, so a paste gets the house parity treatment with no argument to
        // make: a string is a type GraphQL has always had.
        var client = AppWithModel(PostingReply);
        var graphql = new GraphQLClient(client);

        var result = await graphql.QueryAsync(
            """
            mutation($text: String!) {
              importText(text: $text, kind: JOB_POSTING) { id status extractedText }
            }
            """,
            new { text = PastedAd });

        Assert.False(result.HasErrors);
        var import = result.Data!.Value.GetProperty("importText");
        Assert.Equal("PARSING", import.GetProperty("status").GetString());
        foreach (var keyword in Keywords)
            Assert.Contains(keyword, import.GetProperty("extractedText").GetString()!);
    }

    // ------------------------------------------------------------- the clip

    [Fact]
    public async Task AVeryLongAd_IsClippedOnConfirm_RatherThan500ing()
    {
        // The latent bug this group was asked to fix while it was in here.
        // job_postings.Description is varchar(20000), and CommitImport falls
        // back to the whole extracted text when the model proposes no
        // description — so an ad longer than the column confirmed into a
        // database error. A paste is what makes it likely: a file that long is
        // rare, ctrl+A over an ad plus its sidebar and comments is not.
        var client = AppWithModel(PostingReply);

        var longAd = PastedAd + string.Concat(Enumerable.Repeat("Extra responsibilities. ", 1200));
        Assert.True(longAd.Length > 20000);

        using var created = await PasteAsync(client, longAd);
        var id = created.RootElement.GetProperty("id").GetGuid();

        (await client.PostAsync($"/imports/{id}/reparse", null, Ct)).EnsureSuccessStatusCode();

        var confirm = await client.PostAsync($"/imports/{id}/confirm", null, Ct);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        await WithDbAsync(async db =>
        {
            var description = await db.JobPostings.Select(p => p.Description).SingleAsync(Ct);
            Assert.Equal(20000, description!.Length);
            // Clipped from the END, so the top of the ad — the part that says
            // what the job is — is what survives.
            Assert.StartsWith("Senior Backend Engineer", description);
        });
    }
}
