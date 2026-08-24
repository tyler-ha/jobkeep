using System.Collections.Concurrent;
using Jobkeep.Models;

namespace Jobkeep.Repositories;

// Zero-setup, zero-cost fallback for local development (no Postgres needed).
// Data resets on restart, and — unlike the Postgres repo — it does NOT dedup
// companies/skills into shared rows; it just stores whatever graph it's handed.
public class InMemoryJobApplicationRepository : IJobApplicationRepository
{
    private readonly ConcurrentDictionary<Guid, JobApplication> _store = new();

    public Task<List<JobApplication>> GetAllAsync()
    {
        var all = _store.Values
            .OrderByDescending(a => a.DateApplied)
            .ToList();
        return Task.FromResult(all);
    }

    public Task<JobApplication?> GetByIdAsync(Guid id)
    {
        _store.TryGetValue(id, out var app);
        return Task.FromResult(app);
    }

    public Task<JobApplication> CreateAsync(JobApplication application)
    {
        _store[application.Id] = application;
        return Task.FromResult(application);
    }

    public Task<JobApplication?> UpdateAsync(Guid id, UpdateJobApplicationRequest update)
    {
        if (!_store.TryGetValue(id, out var existing))
            return Task.FromResult<JobApplication?>(null);

        if (update.Status is not null) existing.Status = update.Status.Value;
        if (update.Notes is not null) existing.Notes = update.Notes;
        if (update.ResumeText is not null) existing.ResumeText = update.ResumeText;
        if (update.Title is not null) existing.Posting.Title = update.Title;
        if (update.Location is not null) existing.Posting.Location = update.Location;
        if (update.Description is not null) existing.Posting.Description = update.Description;
        if (update.Company is not null) existing.Posting.Company.Name = update.Company;
        existing.UpdatedAtUtc = DateTime.UtcNow;

        return Task.FromResult<JobApplication?>(existing);
    }

    public Task<JobApplication?> AddSkillToPostingAsync(
        Guid applicationId, string skillName, string? category, bool isRequired)
    {
        if (!_store.TryGetValue(applicationId, out var app))
            return Task.FromResult<JobApplication?>(null);

        if (!app.Posting.PostingSkills.Any(ps => ps.Skill.Name == skillName))
        {
            app.Posting.PostingSkills.Add(new PostingSkill
            {
                Skill = new Skill { Name = skillName, Category = category },
                IsRequired = isRequired
            });
        }
        return Task.FromResult<JobApplication?>(app);
    }

    public Task<bool> DeleteAsync(Guid id)
    {
        return Task.FromResult(_store.TryRemove(id, out _));
    }
}
