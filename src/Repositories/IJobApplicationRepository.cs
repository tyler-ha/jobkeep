using Jobkeep.Models;

namespace Jobkeep.Repositories;

// The Phase 1/2 storage contract, with exactly one implementation:
// PostgresJobApplicationRepository. (An earlier comment here claimed Phase 2
// would swap in a DynamoDB implementation behind this interface. It did not —
// Phase 2 chose PostgreSQL. See architecture.md decision 1.)
//
// RETIRING, NOT GROWING (architecture.md decision 5). Do not add methods here:
// new use cases go in src/Modules/ as vertical slices. Phase 2.1 removed the one
// use-case method that had already crept in, AddSkillToPostingAsync — it now
// lives in Modules/Applications/AddSkillToPosting.cs. What's left is genuine
// CRUD, and it goes as Phase 2.2 rewrites the read path.
public interface IJobApplicationRepository
{
    Task<List<JobApplication>> GetAllAsync();
    Task<JobApplication?> GetByIdAsync(Guid id);
    Task<JobApplication> CreateAsync(JobApplication application);
    Task<JobApplication?> UpdateAsync(Guid id, UpdateJobApplicationRequest update);
    Task<bool> DeleteAsync(Guid id);
}
