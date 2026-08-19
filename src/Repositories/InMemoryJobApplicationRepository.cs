using System.Collections.Concurrent;
using Jobkeep.Models;

namespace Jobkeep.Repositories;

// Zero-cost, zero-setup storage for local development.
// Data resets every time you stop the app — that's expected for Phase 1.
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

        if (update.Company is not null) existing.Company = update.Company;
        if (update.Role is not null) existing.Role = update.Role;
        if (update.Status is not null) existing.Status = update.Status.Value;
        if (update.Notes is not null) existing.Notes = update.Notes;
        if (update.JobDescription is not null) existing.JobDescription = update.JobDescription;

        return Task.FromResult<JobApplication?>(existing);
    }

    public Task<bool> DeleteAsync(Guid id)
    {
        return Task.FromResult(_store.TryRemove(id, out _));
    }
}
