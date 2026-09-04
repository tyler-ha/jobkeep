using System.Net.Http.Json;
using System.Text.Json;
using Jobkeep.Modules.Applications;

namespace Jobkeep.Tests.Infrastructure;

/// <summary>
/// Arrange helpers that go through the real HTTP surface rather than seeding rows
/// directly. Arranging through the API means a test's setup exercises the same
/// create path the app ships, so a break there fails loudly everywhere instead of
/// being masked by hand-built entities.
/// </summary>
public static class ApiHelpers
{
    /// <summary>POST /applications and return the new application's id.</summary>
    public static async Task<Guid> CreateApplicationAsync(
        this HttpClient client,
        string company,
        string title,
        CancellationToken ct,
        string? location = null,
        string? description = null,
        string? notes = null,
        Guid? resumeId = null)
    {
        var response = await client.PostAsJsonAsync("/applications", new
        {
            company,
            title,
            location,
            description,
            notes,
            resumeId,
        }, ct);

        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>POST /applications/{id}/skills.</summary>
    public static Task<HttpResponseMessage> AddSkillAsync(
        this HttpClient client,
        Guid applicationId,
        string skillName,
        CancellationToken ct,
        string? category = null,
        bool isRequired = false)
        => client.PostAsJsonAsync($"/applications/{applicationId}/skills",
            new { skillName, category, isRequired }, ct);

    /// <summary>POST /applications/{id}/requirements.</summary>
    public static Task<HttpResponseMessage> AddRequirementAsync(
        this HttpClient client,
        Guid applicationId,
        string text,
        CancellationToken ct,
        string kind = "Qualification",
        bool isMustHave = false)
        => client.PostAsJsonAsync($"/applications/{applicationId}/requirements",
            new { text, kind, isMustHave }, ct);

    /// <summary>
    /// GET /applications/{id} as a raw JsonElement.
    ///
    /// Still raw rather than typed, but for a different reason than before Phase 2.3.
    /// It used to be raw because the read path returned EF entities and there was no DTO
    /// to bind to (architecture.md A2). There is one now — ApplicationDetail — and
    /// binding to it would make these assertions read better. It stays raw deliberately:
    /// deserializing into the app's own record proves the app can round-trip its own
    /// type, not that the JSON on the wire has the field names a client expects. The
    /// wire format is the contract, so the tests read the wire format.
    /// </summary>
    public static async Task<JsonDocument> GetApplicationAsync(
        this HttpClient client, Guid id, CancellationToken ct)
    {
        var response = await client.GetAsync($"/applications/{id}", ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}
