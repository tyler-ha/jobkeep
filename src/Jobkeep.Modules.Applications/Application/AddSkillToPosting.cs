using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Modules.Skills;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: attach a skill to an application's posting, reusing the shared `skills`
// row when the name already exists (find-or-create by name).
//
// This use case used to be a method on IJobApplicationRepository — a *use case*
// bolted onto a CRUD interface, which is architecture.md A3. It now owns itself
// end to end: request, rule, data access and response in one file, called by
// both API surfaces.
//
// The three types are top-level rather than nested inside one static class,
// which is the more common vertical-slice shape. HotChocolate names GraphQL
// types after the CLR type, so four slices each nesting a `Request` would
// collide in the published schema.

public record AddSkillToPostingRequest(string SkillName, string? Category, bool IsRequired);

// A response DTO, not the EF entity (architecture.md A2). The caller gets back
// the link it just made, without dragging the posting -> company -> postings
// navigation cycle along with it.
public record PostingSkillResponse(string SkillName, string? Category, bool IsRequired, SkillSource Source);

public class AddSkillToPostingHandler
{
    private readonly IApplicationsDbContext _db;
    private readonly ISkillCatalog _skills;

    // The handler takes a DbContext directly: EF's DbContext is already a
    // unit-of-work plus a repository, so a hand-written repository over it would
    // be a layer that mostly forwards calls (architecture.md §2, rule 1).
    //
    // Phase 13.2d narrowed it from AppDbContext to IApplicationsDbContext, which
    // exposes this module's five DbSets and nothing else. `skills` is not among
    // them, which is why the catalog is here too.
    public AddSkillToPostingHandler(IApplicationsDbContext db, ISkillCatalog skills)
    {
        _db = db;
        _skills = skills;
    }

    public async Task<SliceResult<PostingSkillResponse>> HandleAsync(
        Guid applicationId, AddSkillToPostingRequest request, CancellationToken ct = default)
    {
        // Validation lives in the handler, not at either edge, so REST and
        // GraphQL cannot enforce different rules (architecture.md A4).
        var skillName = request.SkillName?.Trim();
        if (string.IsNullOrEmpty(skillName))
            return SliceResult<PostingSkillResponse>.Invalid("skillName is required.");

        // Project straight to the FK instead of loading the aggregate: this slice
        // writes one join row and has no use for the posting's other children.
        // The old repository path eager-loaded company + skills + requirements +
        // AI analysis to add a single row.
        var postingId = await _db.JobApplications
            .Where(a => a.Id == applicationId)
            .Select(a => a.PostingId)
            .FirstOrDefaultAsync(ct);
        if (postingId == Guid.Empty)
            return SliceResult<PostingSkillResponse>.NotFound($"Application {applicationId} not found.");

        // Reuse the shared skill row if it exists — this is the dedup that makes
        // "top skills across all my tracked jobs" a single GROUP BY over `skills`,
        // and the reason Postgres was chosen over DynamoDB (decision 1).
        //
        // Phase 13.2d — through ISkillCatalog, which owns the natural key Phase 7
        // introduced. This slice no longer knows the key exists, which is the
        // point: it was one of four places that each had to remember, and
        // forgetting turned an ordinary name into a 500.
        //
        // The catalog SAVES, and it is called before the link row is built for
        // that reason (ISkillCatalog.FindOrCreateAsync says why at length). All
        // six context interfaces still resolve one scoped AppDbContext, so a save
        // in there flushes anything pending here.
        var resolved = await _skills.FindOrCreateAsync([new SkillRequest(skillName, request.Category)], ct);
        var skill = resolved[skillName];

        var link = await _db.PostingSkills
            .FirstOrDefaultAsync(ps => ps.PostingId == postingId && ps.SkillId == skill.Id, ct);
        if (link is null)
        {
            link = new PostingSkill
            {
                PostingId = postingId,
                SkillId = skill.Id,
                IsRequired = request.IsRequired,
                Source = SkillSource.Parsed   // human-entered; Phase 4 writes AiExtracted
            };
            _db.PostingSkills.Add(link);
            await _db.SaveChangesAsync(ct);
        }
        // else the pair is already linked, and adding it again is a no-op rather
        // than an error. The composite PK on posting_skills makes "at most once
        // per posting" the schema's rule, so a client retrying a POST is asking
        // for a state that already holds — 200, not 400.

        return SliceResult<PostingSkillResponse>.Ok(
            new PostingSkillResponse(skill.Name, skill.Category, link.IsRequired, link.Source));
    }
}
