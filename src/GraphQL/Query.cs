using Jobkeep.Models;
using Jobkeep.Repositories;

namespace Jobkeep.GraphQL;

// GraphQL read side. Resolvers are deliberately thin — they reuse the exact same
// IJobApplicationRepository the REST endpoints use, so both API surfaces sit on
// one storage implementation. HotChocolate infers the whole object graph
// (JobApplication -> JobPosting -> Company / PostingSkills -> Skill / ...) from
// the return types, so clients can ask for exactly the nested fields they want.
public class Query
{
    public Task<List<JobApplication>> GetApplications([Service] IJobApplicationRepository repo)
        => repo.GetAllAsync();

    public Task<JobApplication?> GetApplication(Guid id, [Service] IJobApplicationRepository repo)
        => repo.GetByIdAsync(id);
}
