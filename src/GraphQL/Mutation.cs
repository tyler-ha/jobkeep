using Jobkeep.Models;
using Jobkeep.Modules.Applications;
using Jobkeep.Repositories;

namespace Jobkeep.GraphQL;

// GraphQL write side. Every mutation here is a thin adapter: it hands the
// request to the same code path REST uses and translates the outcome. The
// create/update/delete trio still goes through the retiring repository; the
// skills and requirements mutations go through Phase 2.1's slice handlers,
// which is the migration in progress made visible in one file.
public class Mutation
{
    public async Task<JobApplication> CreateApplication(
        CreateJobApplicationRequest input, [Service] IJobApplicationRepository repo)
    {
        var application = new JobApplication
        {
            Notes = input.Notes,
            ResumeText = input.ResumeText,
            Posting = new JobPosting
            {
                Title = input.Title,
                Location = input.Location,
                Description = input.Description,
                SourceUrl = input.SourceUrl,
                Company = new Company { Name = input.Company }
            }
        };
        return await repo.CreateAsync(application);
    }

    public Task<JobApplication?> UpdateApplication(
        Guid id, UpdateJobApplicationRequest input, [Service] IJobApplicationRepository repo)
        => repo.UpdateAsync(id, input);

    public Task<bool> DeleteApplication(Guid id, [Service] IJobApplicationRepository repo)
        => repo.DeleteAsync(id);

    // Exercises the shared-skills join: reuses an existing Skill row by name or
    // creates it, then links it to the application's posting.
    //
    // Returns the link that was made rather than the whole re-read application.
    // A GraphQL client that wants the aggregate back can ask for it in a follow-up
    // query; returning it unconditionally is the over-fetch this phase is moving
    // away from (architecture.md A1/A2).
    public async Task<PostingSkillResponse> AddSkillToPosting(
        Guid applicationId, AddSkillToPostingRequest input,
        [Service] AddSkillToPostingHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(applicationId, input, ct)).ValueOrThrow();

    // Unlinks the posting_skills join row; the shared `skills` row survives.
    public async Task<bool> RemoveSkillFromPosting(
        Guid applicationId, string skillName,
        [Service] RemoveSkillFromPostingHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(applicationId, skillName, ct)).ValueOrThrow();

    public async Task<RequirementResponse> AddRequirementToPosting(
        Guid applicationId, AddRequirementToPostingRequest input,
        [Service] AddRequirementToPostingHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(applicationId, input, ct)).ValueOrThrow();

    public async Task<bool> RemoveRequirement(
        Guid applicationId, Guid requirementId,
        [Service] RemoveRequirementHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(applicationId, requirementId, ct)).ValueOrThrow();
}
