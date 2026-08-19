using Jobkeep.Models;

namespace Jobkeep.Repositories;

// Phase 1 implements this in memory. Phase 2 swaps in a DynamoDB
// implementation behind the same interface — the API endpoints
// in Program.cs never need to change.
public interface IJobApplicationRepository
{
    Task<List<JobApplication>> GetAllAsync();
    Task<JobApplication?> GetByIdAsync(Guid id);
    Task<JobApplication> CreateAsync(JobApplication application);
    Task<JobApplication?> UpdateAsync(Guid id, UpdateJobApplicationRequest update);
    Task<bool> DeleteAsync(Guid id);
}
