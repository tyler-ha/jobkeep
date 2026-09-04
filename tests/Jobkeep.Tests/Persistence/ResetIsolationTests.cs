using Jobkeep.Modules.Ai.Domain;
using Jobkeep.Modules.Applications.Domain;
using Jobkeep.Modules.Match.Domain;
using Jobkeep.Modules.Documents.Domain;
using Jobkeep.Modules.Skills.Domain;
using Jobkeep.Tests.Infrastructure;

namespace Jobkeep.Tests.Persistence;

/// <summary>
/// Proves that <see cref="PostgresFixture.ResetAsync"/> actually empties the database.
///
/// <para>
/// This test exists because of how the alternative fails. Respawn is configured with an
/// explicit list of schemas, and until 13.3b that list was the single literal
/// <c>["public"]</c>. The split moved all thirteen tables into five module schemas, and
/// a Respawner pointed at a schema with no tables in it does not throw, warn, or return
/// anything unusual — <b>it truncates nothing and reports success</b>. Every test would
/// then inherit the previous test's rows.
/// </para>
///
/// <para>
/// That surfaces as cross-test flakiness with no obvious cause: a suite that passes
/// alone and fails in order, or worse, one where a stale row happens to satisfy the next
/// assertion. It is the most expensive failure mode a test harness has, and it is
/// invisible to the compiler, so the fix ships with an assertion rather than with
/// confidence.
/// </para>
///
/// <para>
/// One table per module schema, deliberately. Getting four of five right is exactly the
/// mistake a hand-maintained list invites, and only a per-schema check would catch it.
/// </para>
/// </summary>
public sealed class ResetIsolationTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task ResetAsync_EmptiesATableInEveryModuleSchema()
    {
        var applicationId = await Client.CreateApplicationAsync("Canva", "Backend Engineer", Ct);
        var postingId = await WithDbAsync(db => db.JobApplications
            .Where(a => a.Id == applicationId).Select(a => a.PostingId).SingleAsync(Ct));

        await WithDbAsync(async db =>
        {
            // skills, and the link that makes it reachable from `applications`.
            var skill = new Skill { Name = "C#" };
            db.Skills.Add(skill);
            db.PostingSkills.Add(new PostingSkill { PostingId = postingId, SkillId = skill.Id });

            // documents.
            db.Resumes.Add(new Resume { Label = "seeded", SourceText = "text" });

            // ai and ats — both are rows that 13.3b made possible to orphan, which is
            // also what makes them easy to seed here without a real workflow.
            db.AiAnalyses.Add(new AiAnalysis { PostingId = postingId, Summary = "seeded" });
            db.MatchResults.Add(new MatchResult { ApplicationId = applicationId });

            await db.SaveChangesAsync(Ct);
        });

        // Every schema now holds at least one row. If this fails, the seed is wrong and
        // the reset assertion below would pass for the wrong reason.
        await WithDbAsync(async db =>
        {
            Assert.Equal(1, await db.JobApplications.CountAsync(Ct));
            Assert.Equal(1, await db.Skills.CountAsync(Ct));
            Assert.Equal(1, await db.Resumes.CountAsync(Ct));
            Assert.Equal(1, await db.AiAnalyses.CountAsync(Ct));
            Assert.Equal(1, await db.MatchResults.CountAsync(Ct));
        });

        await Fixture.ResetAsync();

        await WithDbAsync(async db =>
        {
            Assert.Equal(0, await db.JobApplications.CountAsync(Ct));   // applications
            Assert.Equal(0, await db.JobPostings.CountAsync(Ct));       // applications
            Assert.Equal(0, await db.Companies.CountAsync(Ct));         // applications
            Assert.Equal(0, await db.PostingSkills.CountAsync(Ct));     // applications
            Assert.Equal(0, await db.Skills.CountAsync(Ct));            // skills
            Assert.Equal(0, await db.Resumes.CountAsync(Ct));           // documents
            Assert.Equal(0, await db.AiAnalyses.CountAsync(Ct));        // ai
            Assert.Equal(0, await db.MatchResults.CountAsync(Ct));        // ats
        });
    }

    [Fact]
    public async Task ResetAsync_LeavesTheSixMigrationHistoriesAlone()
    {
        // The other half of the configuration, and the half that fails loudly rather
        // than silently: wiping a history table makes EF think that module is
        // unmigrated. Each entry in TablesToIgnore has to be SCHEMA-QUALIFIED, because
        // an unqualified Table() means the default schema and would leave the other four
        // to be truncated.
        await Fixture.ResetAsync();

        foreach (var schema in PostgresFixture.ModuleSchemas)
        {
            var applied = await ScalarAsync(
                $"""SELECT COUNT(*)::int FROM "{schema}"."__EFMigrationsHistory";""");

            // NOT an exact count. This asserted `== 1` until Phase 14 gave the
            // Skills module a second migration, and the failure was a false alarm:
            // the property under test is that the history SURVIVES the reset, and
            // the number of migrations a module has is nobody's business here. An
            // exact count turns every future migration into a broken test in a file
            // about Respawn configuration, which is how a test stops being read and
            // starts being edited until it passes.
            Assert.True(Assert.IsType<int>(applied) > 0,
                $"{schema}.__EFMigrationsHistory was truncated by ResetAsync.");
        }
    }
}
