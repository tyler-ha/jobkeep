using System.Net;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Rest;

public sealed class SmokeTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task GetApplications_OnEmptyDatabase_Returns200AndAnEmptyPage()
    {
        var response = await Client.GetAsync("/applications", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Phase 2.3 turned the bare array into a paged envelope. Asserting the whole
        // body rather than just items.length pins the default page size and the fact
        // that an empty result is still a well-formed page — a client reading
        // totalPages should get 0, not null and not 1.
        Assert.Equal(
            """{"items":[],"totalCount":0,"page":1,"pageSize":20,"totalPages":0}""",
            await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Migrations_AppliedCleanly_SoAllEightTablesExist()
    {
        // The fixture booting Program.cs in Development is what runs Database.Migrate().
        // This asserts the result rather than the act, so a migration that stops applying
        // fails here loudly instead of surfacing as a confusing error in another test.
        var tables = await WithDbAsync(async db =>
        {
            var conn = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(Ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "select table_name from information_schema.tables where table_schema = 'public' order by 1";
            using var reader = await cmd.ExecuteReaderAsync(Ct);

            var names = new List<string>();
            while (await reader.ReadAsync(Ct))
            {
                names.Add(reader.GetString(0));
            }

            return names;
        });

        Assert.Contains("__EFMigrationsHistory", tables);
        Assert.Contains("companies", tables);
        Assert.Contains("job_postings", tables);
        Assert.Contains("skills", tables);
        Assert.Contains("posting_skills", tables);
        Assert.Contains("job_requirements", tables);
        Assert.Contains("job_applications", tables);
        Assert.Contains("ai_analyses", tables);
        Assert.Contains("ats_results", tables);
    }
}
