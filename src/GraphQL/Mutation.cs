using Jobkeep.Models;
using Jobkeep.Repositories;

namespace Jobkeep.GraphQL;

// GraphQL write side. Same repository as REST — no duplicated business logic.
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
    public Task<JobApplication?> AddSkillToPosting(
        Guid applicationId, string skillName, string? category, bool isRequired,
        [Service] IJobApplicationRepository repo)
        => repo.AddSkillToPostingAsync(applicationId, skillName, category, isRequired);
}
