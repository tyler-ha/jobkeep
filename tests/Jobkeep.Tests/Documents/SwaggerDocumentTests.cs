using System.Net;
using System.Text.Json;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Documents;

/// <summary>
/// The OpenAPI document generates, and the upload endpoint is in it.
///
/// <para>
/// Written after a real outage of the only human-facing surface this app has.
/// Swashbuckle 10 refuses an action carrying both an <c>IFormFile</c> and a
/// <c>[FromForm]</c> parameter, and it refuses by <em>throwing</em> rather than by
/// skipping the operation — so <c>POST /imports</c> alone made
/// <c>GET /swagger/v1/swagger.json</c> answer 500, and Swagger UI showed
/// "Fetch error" for every endpoint in the app. Nothing failed a build, nothing
/// failed a test, and the suite was 212 green while <c>/swagger</c> was unusable.
/// It shipped in Phase 4.5 and was found by hand at the end of Phase 5.
/// </para>
///
/// <para>
/// So this is the counterpart to the rule already recorded against the committed
/// SVG diagrams: <strong>a generated artefact with no build step behind it goes
/// stale silently.</strong> The diagrams are redrawn on a trigger because nothing
/// can check them; this one <em>can</em> be checked, so it is.
/// </para>
///
/// <para>
/// Deliberately not a snapshot of the whole document. Asserting on the full JSON
/// would fail on every new route and teach everyone to re-baseline it without
/// reading, which is a test that costs attention and buys nothing. What is pinned
/// is the property that actually broke: the document generates at all, and the
/// one route that has ever been unrepresentable is representable.
/// </para>
/// </summary>
public class SwaggerDocumentTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task SwaggerJson_Generates()
    {
        var response = await Client.GetAsync("/swagger/v1/swagger.json", Ct);

        // The failure this pins answers 500 with a SwaggerGeneratorException, so
        // the status code alone is the whole assertion — but read the body into
        // the message, because "expected OK, got InternalServerError" on its own
        // sends the next person to the logs for something already in hand.
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET /swagger/v1/swagger.json returned {(int)response.StatusCode}. "
          + $"Body: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    [Fact]
    public async Task SwaggerJson_DescribesTheMultipartUpload_AndTheRoutesAroundIt()
    {
        using var doc = JsonDocument.Parse(
            await Client.GetStringAsync("/swagger/v1/swagger.json", Ct));

        var paths = doc.RootElement.GetProperty("paths");

        // The route that broke it: a multipart POST whose body is a file plus
        // three text fields.
        var upload = paths.GetProperty("/imports").GetProperty("post");
        Assert.True(
            upload.GetProperty("requestBody").GetProperty("content")
                  .TryGetProperty("multipart/form-data", out _),
            "POST /imports is in the document but not described as multipart/form-data.");

        // And a couple of ordinary routes either side of it, because the failure
        // mode was never local to one operation — the generator throws and the
        // whole document goes with it.
        Assert.True(paths.TryGetProperty("/applications", out _));
        Assert.True(paths.TryGetProperty("/resumes/{id}/skills", out _));
        Assert.True(paths.TryGetProperty("/applications/{id}/match-check", out _));
    }
}
