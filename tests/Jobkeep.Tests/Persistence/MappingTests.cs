using Jobkeep.Contracts.Shared;
using Jobkeep.Modules.Applications.Domain;
using Jobkeep.Modules.Ats.Domain;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Persistence;

/// <summary>
/// EF-to-Postgres mapping: the assertions that only mean anything against a real
/// provider.
///
/// Every test in here would either pass vacuously or throw on EF's InMemory provider.
/// InMemory has no notion of column types, so text[] and numeric(12,2) are just CLR
/// objects; enum-as-string is invisible because the CLR value round-trips regardless.
/// This class is the concrete answer to "why Testcontainers instead of a fake".
/// </summary>
public sealed class MappingTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task EnumsArePersistedAsText_NotAsIntegers()
    {
        await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);

        // 13.3b: every raw-SQL table name in this suite is schema-qualified now.
        // An unqualified name resolves through search_path, which is `public` — the
        // one schema that no longer holds a table.
        var status = await ScalarAsync("select \"Status\" from applications.job_applications limit 1");
        var employmentType = await ScalarAsync("select \"EmploymentType\" from applications.job_postings limit 1");
        var salaryPeriod = await ScalarAsync("select \"SalaryPeriod\" from applications.job_postings limit 1");

        // Reading these through EF would return ApplicationStatus.Applied either way.
        // Only the raw column proves HasConversion<string>() is actually in effect.
        Assert.Equal("Applied", status);
        Assert.Equal("FullTime", employmentType);
        Assert.Equal("Year", salaryPeriod);
    }

    [Fact]
    public async Task EnumColumnsAreVarcharNotText_SoTheStringLengthCapsAreReal()
    {
        var statusType = await ScalarAsync(
            "select data_type || '(' || coalesce(character_maximum_length::text, '?') || ')' " +
            "from information_schema.columns " +
            "where table_schema = 'applications' and table_name = 'job_applications' "
            + "and column_name = 'Status'");

        Assert.Equal("character varying(20)", statusType);
    }

    [Fact]
    public async Task SalaryPrecisionIsNumeric12_2_SoExtraDecimalsAreRounded()
    {
        var id = await Client.CreateApplicationAsync("Atlassian", "Backend Engineer", Ct);
        var postingId = await WithDbAsync(db =>
            db.JobApplications.Where(a => a.Id == id).Select(a => a.PostingId).SingleAsync(Ct));

        await WithDbAsync(async db =>
        {
            var posting = await db.JobPostings.SingleAsync(p => p.Id == postingId, Ct);
            posting.SalaryMin = 123456.789m;
            posting.SalaryMax = 150000.004m;
            await db.SaveChangesAsync(Ct);
        });

        // numeric(12,2) rounds at the database, so the value read back is not the value
        // written. Money silently losing precision is worth a test.
        var min = await ScalarAsync("select \"SalaryMin\" from applications.job_postings limit 1");
        var max = await ScalarAsync("select \"SalaryMax\" from applications.job_postings limit 1");

        Assert.Equal(123456.79m, min);
        Assert.Equal(150000.00m, max);
    }

    [Fact]
    public async Task DateAppliedIsADateColumn_WithNoTimeComponent()
    {
        await Client.CreateApplicationAsync("Xero", "Senior Engineer", Ct);

        var columnType = await ScalarAsync(
            "select data_type from information_schema.columns " +
            "where table_schema = 'applications' and table_name = 'job_applications' "
            + "and column_name = 'DateApplied'");

        Assert.Equal("date", columnType);

        // The corollary, and the reason no test in this suite asserts list ordering:
        // DateOnly has day granularity, so applications created in the same test run
        // are all the same date and their relative order is undefined.
        var applied = await WithDbAsync(db =>
            db.JobApplications.Select(a => a.DateApplied).SingleAsync(Ct));
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), applied);
    }

    [Fact]
    public async Task AtsKeywordListsRoundTripThroughPostgresTextArrays()
    {
        // Three text[] columns, which is a Postgres-specific mapping with no equivalent
        // in the InMemory or SQLite providers. Phase 5 depends on this working.
        var id = await Client.CreateApplicationAsync("Seek", "Engineer", Ct);
        await WithDbAsync(async db =>
        {
            db.AtsResults.Add(new AtsResult
            {
                ApplicationId = id,
                MatchedKeywords = ["C#", "PostgreSQL", "AWS"],
                MissingMustHaveKeywords = ["Kubernetes"],
                FormattingRiskNotes = [],
            });
            await db.SaveChangesAsync(Ct);
        });

        var columnType = await ScalarAsync(
            "select udt_name from information_schema.columns " +
            "where table_schema = 'ats' and table_name = 'ats_results' "
            + "and column_name = 'MatchedKeywords'");
        Assert.Equal("_text", columnType);

        var result = await WithDbAsync(db => db.AtsResults.SingleAsync(Ct));
        Assert.Equal(["C#", "PostgreSQL", "AWS"], result.MatchedKeywords);
        Assert.Equal(["Kubernetes"], result.MissingMustHaveKeywords);
        Assert.Empty(result.FormattingRiskNotes);
    }

    [Fact]
    public async Task PostingSkillsUsesACompositePrimaryKey_NotASurrogateId()
    {
        // The composite key is what makes "add this skill twice" a no-op rather than a
        // duplicate row, which is why the idempotent-add behaviour is safe.
        var id = await Client.CreateApplicationAsync("REA Group", "Engineer", Ct);
        (await Client.AddSkillAsync(id, "C#", Ct)).EnsureSuccessStatusCode();

        var keyColumns = await ScalarAsync(
            "select string_agg(a.attname, ',' order by a.attname) " +
            "from pg_index i " +
            "join pg_attribute a on a.attrelid = i.indrelid and a.attnum = any(i.indkey) " +
            // regclass resolves through search_path too, so it needs the schema just
            // as much as a FROM clause does — and fails at runtime rather than parse.
            "where i.indrelid = 'applications.posting_skills'::regclass and i.indisprimary");

        Assert.Equal("PostingId,SkillId", keyColumns);
    }

    [Fact]
    public async Task OneToOneRelationshipsAreEnforcedByUniqueIndexes()
    {
        // ai_analyses and ats_results are 1:1 with their parent. That is a unique index,
        // not a convention, and InMemory would not enforce it.
        var id = await Client.CreateApplicationAsync("Telstra", "Engineer", Ct);

        await WithDbAsync(async db =>
        {
            db.AtsResults.Add(new AtsResult { ApplicationId = id });
            await db.SaveChangesAsync(Ct);
        });

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => WithDbAsync(async db =>
        {
            db.AtsResults.Add(new AtsResult { ApplicationId = id });
            await db.SaveChangesAsync(Ct);
        }));

        Assert.Contains("duplicate key", failure.InnerException?.Message ?? "");
    }
}
