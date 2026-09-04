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
    public async Task Migrations_AppliedCleanly_SoEveryModuleOwnsItsOwnSchema()
    {
        // The fixture booting Program.cs in Development is what runs Database.Migrate()
        // on each context. This asserts the result rather than the act, so a migration
        // that stops applying fails here loudly instead of surfacing as a confusing
        // error in another test.
        //
        // PHASE 13.3b turned this from a list of eight tables in `public` into a map of
        // schema to table, and the shape of the assertion is the deliverable: it now
        // fails if a table lands in the wrong schema, which is the mistake the split
        // makes possible and nothing else would catch. A table in the wrong place still
        // works — one database, one connection — right up until the module is extracted.
        var tables = await WithDbAsync(async db =>
        {
            var conn = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(Ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "select table_schema || '.' || table_name from information_schema.tables "
                + "where table_schema not in ('pg_catalog', 'information_schema') order by 1";
            using var reader = await cmd.ExecuteReaderAsync(Ct);

            var names = new List<string>();
            while (await reader.ReadAsync(Ct))
            {
                names.Add(reader.GetString(0));
            }

            return names;
        });

        // Applications: five tables plus the three views it publishes.
        Assert.Contains("applications.companies", tables);
        Assert.Contains("applications.job_postings", tables);
        Assert.Contains("applications.posting_skills", tables);
        Assert.Contains("applications.job_requirements", tables);
        Assert.Contains("applications.job_applications", tables);
        Assert.Contains("applications.v_application_status_counts", tables);
        Assert.Contains("applications.v_company_application_counts", tables);
        Assert.Contains("applications.v_posting_skill_demand", tables);

        // The shared taxonomy, alone in its own schema — the whole argument for the
        // Skills module in one line.
        Assert.Contains("skills.skills", tables);

        Assert.Contains("documents.document_imports", tables);
        Assert.Contains("documents.resumes", tables);
        Assert.Contains("documents.resume_skills", tables);
        Assert.Contains("documents.resume_experiences", tables);
        Assert.Contains("documents.resume_educations", tables);

        Assert.Contains("ai.ai_analyses", tables);
        Assert.Contains("ats.match_results", tables);

        // Five histories, one per table-owning context. Analytics has none because it
        // owns nothing to create, and a sixth appearing here would mean it had started
        // to.
        foreach (var schema in PostgresFixture.ModuleSchemas)
            Assert.Contains($"{schema}.__EFMigrationsHistory", tables);

        Assert.DoesNotContain("analytics.__EFMigrationsHistory", tables);

        // And nothing is left in `public`. That is what proves the split is complete
        // rather than additive: a table the migration reset forgot would still be here.
        Assert.DoesNotContain(tables, name => name.StartsWith("public."));
    }
}
