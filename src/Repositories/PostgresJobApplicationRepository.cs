using Jobkeep.Data;
using Jobkeep.Models;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Repositories;

// Phase 2 storage: PostgreSQL via EF Core, behind the same interface the rest
// of the app already depends on. The endpoints/resolvers never see EF Core.
//
// Registered SCOPED (not singleton) because it holds an AppDbContext, which is
// itself scoped per-request — a singleton capturing a scoped context would be a
// classic captive-dependency bug.
public class PostgresJobApplicationRepository : IJobApplicationRepository
{
    private readonly AppDbContext _db;

    public PostgresJobApplicationRepository(AppDbContext db) => _db = db;

    // One reusable include graph so every read returns the full aggregate.
    // AsSplitQuery avoids a cartesian blow-up from JOINing several collections
    // at once (posting skills + requirements) into one wide result set.
    private IQueryable<JobApplication> WithGraph() =>
        _db.JobApplications
            .Include(a => a.Posting).ThenInclude(p => p.Company)
            .Include(a => a.Posting).ThenInclude(p => p.PostingSkills).ThenInclude(ps => ps.Skill)
            .Include(a => a.Posting).ThenInclude(p => p.Requirements)
            .Include(a => a.Posting).ThenInclude(p => p.AiAnalysis)
            .Include(a => a.AtsResult)
            .AsSplitQuery();

    public async Task<List<JobApplication>> GetAllAsync() =>
        await WithGraph().OrderByDescending(a => a.DateApplied).ToListAsync();

    public async Task<JobApplication?> GetByIdAsync(Guid id) =>
        await WithGraph().FirstOrDefaultAsync(a => a.Id == id);

    public async Task<JobApplication> CreateAsync(JobApplication application)
    {
        // Resolve the shared Company / Skill rows by natural key BEFORE inserting,
        // so we reuse existing rows instead of tripping the unique-name indexes.
        application.Posting.Company = await ResolveCompanyAsync(application.Posting.Company);
        application.Posting.CompanyId = application.Posting.Company.Id;
        await ResolveSkillsAsync(application.Posting.PostingSkills);

        _db.JobApplications.Add(application);
        await _db.SaveChangesAsync();

        // Re-read through the include graph so the caller gets the full aggregate.
        return await GetByIdAsync(application.Id) ?? application;
    }

    public async Task<JobApplication?> UpdateAsync(Guid id, UpdateJobApplicationRequest update)
    {
        var app = await _db.JobApplications
            .Include(a => a.Posting).ThenInclude(p => p.Company)
            .FirstOrDefaultAsync(a => a.Id == id);
        if (app is null) return null;

        // Application-level fields.
        if (update.Status is not null) app.Status = update.Status.Value;
        if (update.Notes is not null) app.Notes = update.Notes;
        if (update.ResumeText is not null) app.ResumeText = update.ResumeText;

        // Posting-level fields.
        if (update.Title is not null) app.Posting.Title = update.Title;
        if (update.Location is not null) app.Posting.Location = update.Location;
        if (update.Description is not null) app.Posting.Description = update.Description;
        if (update.Company is not null)
        {
            var company = await ResolveCompanyAsync(new Company { Name = update.Company });
            app.Posting.Company = company;
            app.Posting.CompanyId = company.Id;
        }

        app.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetByIdAsync(id);
    }

    // AddSkillToPostingAsync used to live here. Phase 2.1 moved it to
    // Modules/Applications/AddSkillToPosting.cs — a use case belongs in a slice,
    // not on a CRUD interface (architecture.md A3, decision 5).

    public async Task<bool> DeleteAsync(Guid id)
    {
        var app = await _db.JobApplications.FindAsync(id);
        if (app is null) return false;

        // ats_results cascades; the posting is deliberately left in place
        // (it may be shared by other applications).
        _db.JobApplications.Remove(app);
        await _db.SaveChangesAsync();
        return true;
    }

    // Find-or-create by name. If the company already exists we reuse the tracked
    // row (filling in any optional detail the caller supplied); otherwise the new
    // instance is returned and inserted as part of the application graph.
    private async Task<Company> ResolveCompanyAsync(Company incoming)
    {
        var existing = await _db.Companies.FirstOrDefaultAsync(c => c.Name == incoming.Name);
        if (existing is null) return incoming;

        existing.Website ??= incoming.Website;
        existing.Industry ??= incoming.Industry;
        existing.HqLocation ??= incoming.HqLocation;
        return existing;
    }

    // Point each join row at the existing shared Skill when one matches by name,
    // so we don't create duplicate skills. Unmatched skills are inserted as new.
    private async Task ResolveSkillsAsync(List<PostingSkill> postingSkills)
    {
        foreach (var ps in postingSkills)
        {
            var existing = await _db.Skills.FirstOrDefaultAsync(s => s.Name == ps.Skill.Name);
            if (existing is not null)
            {
                ps.Skill = existing;
                ps.SkillId = existing.Id;
            }
        }
    }
}
