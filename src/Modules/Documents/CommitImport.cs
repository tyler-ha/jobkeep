using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Modules.Applications;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// Slice: the user confirms the draft, and it becomes real rows.
//
// ---------------------------------------------------------------------------
// The two halves commit through different machinery, on purpose
// ---------------------------------------------------------------------------
// A resume commits by writing this module's own tables. A job posting commits by
// calling the Applications module's existing use cases. That asymmetry is the
// interesting design decision in this file, and it follows from who owns what:
//
//   * `resumes` and its children are Documents-owned. Nobody else writes them.
//   * `job_applications`, `job_postings` and `job_requirements` are
//     Applications-owned, and creating one has RULES — company and title are
//     required, the company name is resolved against a unique index rather than
//     inserted blind. Those rules live in CreateApplicationHandler.
//
// So the posting half calls that handler rather than reimplementing it. This is
// **not** the rule-2 crossing that IPostingContract exists to mediate: reaching
// into another module's *tables* is what rule 2 forbids, and invoking its
// published *use case* is the opposite of that — it is the boundary working. The
// validation runs, the company dedup runs, and there is exactly one implementation
// of "what it means to create an application", which is the property architecture.md
// A4 was written to protect.
//
// The one place this file does touch Applications-owned tables is the skills,
// and it goes through IPostingContract.AddExtractedSkillsAsync — the contract
// Phase 4 built for precisely this, used as-is. Its two-method cap is not
// stretched: a second consumer needing exactly the methods that already exist is
// evidence the boundary was drawn in the right place, not pressure to move it.

public record CommitResponse(
    Guid ImportId,
    DocumentKind Kind,
    Guid CommittedEntityId,
    string Description,
    int SkillsLinked,
    int ExperiencesCreated,
    int EducationsCreated,
    int RequirementsCreated);

public class CommitImportHandler
{
    private readonly AppDbContext _db;
    private readonly CreateApplicationHandler _createApplication;
    private readonly AddRequirementToPostingHandler _addRequirement;
    private readonly IPostingContract _postings;

    public CommitImportHandler(
        AppDbContext db,
        CreateApplicationHandler createApplication,
        AddRequirementToPostingHandler addRequirement,
        IPostingContract postings)
    {
        _db = db;
        _createApplication = createApplication;
        _addRequirement = addRequirement;
        _postings = postings;
    }

    public async Task<SliceResult<CommitResponse>> HandleAsync(Guid id, CancellationToken ct = default)
    {
        var import = await _db.DocumentImports.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (import is null)
            return SliceResult<CommitResponse>.NotFound($"Import {id} not found.");

        // Committing twice would create a second resume from the same draft. The
        // status check is what makes the confirm button safe to double-click,
        // which is not a hypothetical on a request that takes seconds.
        if (import.Status != ImportStatus.AwaitingReview)
            return SliceResult<CommitResponse>.Invalid(
                $"This import is already {import.Status.ToString().ToLowerInvariant()}.");

        var draft = ImportDocumentHandler.ReadDraft(import);

        return import.Kind switch
        {
            DocumentKind.Resume => await CommitResumeAsync(import, draft.Resume, ct),
            _ => await CommitPostingAsync(import, draft.Posting, ct)
        };
    }

    private async Task<SliceResult<CommitResponse>> CommitResumeAsync(
        DocumentImport import, ResumeDraft? draft, CancellationToken ct)
    {
        if (draft is null)
            return SliceResult<CommitResponse>.Invalid(
                "This import has no resume draft to commit. Re-parse it or fill the draft in first.");

        var label = draft.Label?.Trim();
        if (string.IsNullOrEmpty(label))
            return SliceResult<CommitResponse>.Invalid(
                "Give this resume a label before confirming it — for example \"backend-focused\".");

        // Checked before the insert rather than caught after, so the user gets a
        // sentence instead of a unique-index violation. Same pattern as
        // CompanyLookup, and the same known limitation: the comparison is
        // case-sensitive, so "Backend" and "backend" are two resumes. That is the
        // dedup gap already recorded against skills and companies; it is left
        // consistent here rather than fixed on one table (CLAUDE.md, Phase 2.7).
        if (await _db.Resumes.AnyAsync(r => r.Label == label, ct))
            return SliceResult<CommitResponse>.Invalid(
                $"A resume labelled \"{label}\" already exists. Pick a different label.");

        var resume = new Resume
        {
            Label = label,
            FullName = draft.FullName,
            Email = draft.Email,
            Phone = draft.Phone,
            Location = draft.Location,
            Headline = draft.Headline,
            // The verbatim extracted text, not the draft. Phase 5's ATS check
            // reads this: comparing a posting against the structured summary
            // would compare it against what the model chose to keep, which is
            // exactly the layer this phase exists to make optional.
            SourceText = import.ExtractedText,
            SourceFileName = import.FileName,
            SourceHash = import.ContentHash
        };

        var ordinal = 0;
        foreach (var experience in draft.Experience)
        {
            resume.Experiences.Add(new ResumeExperience
            {
                Employer = experience.Employer.Trim(),
                Title = experience.Title,
                StartText = experience.Start,
                EndText = experience.End,
                Highlights = experience.Highlights,
                Ordinal = ordinal++
            });
        }

        ordinal = 0;
        foreach (var education in draft.Education)
        {
            resume.Educations.Add(new ResumeEducation
            {
                Institution = education.Institution.Trim(),
                Qualification = education.Qualification,
                YearText = education.Year,
                Ordinal = ordinal++
            });
        }

        _db.Resumes.Add(resume);

        // Skills last, and through the shared table. Whether the row is created
        // or reused is the entire reason `skills` is shared: once your resume's
        // "C#" is the same row as a posting's "C#", "what do the jobs I want ask
        // for that my resume never mentions" is a join. That query is Phase 5.
        var linked = await LinkSkillsAsync(resume, draft.Skills, import.ModelUsed is not null, ct);

        import.Status = ImportStatus.Committed;
        import.CommittedAtUtc = DateTime.UtcNow;
        import.UpdatedAtUtc = import.CommittedAtUtc.Value;
        import.CommittedEntityId = resume.Id;

        // One SaveChanges for the resume, its children, the new skill rows, the
        // links and the import's status change. They are one user action, so they
        // are one transaction: a commit that half-applied would leave a resume
        // with no skills and an import that still says it needs reviewing.
        await _db.SaveChangesAsync(ct);

        return SliceResult<CommitResponse>.Ok(new CommitResponse(
            import.Id,
            import.Kind,
            resume.Id,
            $"Saved resume \"{resume.Label}\".",
            linked,
            resume.Experiences.Count,
            resume.Educations.Count,
            0));
    }

    // Find-or-create against the shared `skills` table.
    //
    // Written here rather than borrowed from Applications because `skills` is not
    // an Applications table — it is the shared vocabulary both sides of the app
    // are deliberately built on, in the same way AppDbContext is shared. What
    // stays module-owned is the LINK table: Applications owns `posting_skills`,
    // Documents owns `resume_skills`, and neither writes the other's.
    private async Task<int> LinkSkillsAsync(
        Resume resume, List<string> names, bool fromModel, CancellationToken ct)
    {
        // Dedup first. A model asked for a skill list will return "C#" twice, and
        // the composite PK on resume_skills turns that into a duplicate-key
        // exception on SaveChanges rather than a no-op.
        var deduped = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (deduped.Count == 0) return 0;

        // One query for the whole batch, then decide in memory — the same shape
        // PostingContract uses, and for the same reason: a per-skill round trip
        // would be one query and one insert per skill.
        var existing = await _db.Skills
            .Where(s => deduped.Contains(s.Name))
            .ToDictionaryAsync(s => s.Name, ct);

        // Provenance follows where the content came from, not who approved it.
        // A draft the model wrote and the user confirmed unchanged is still
        // AiExtracted; a draft the user edited cleared ModelUsed (ReviewImport)
        // and is Parsed. Confirming is not authorship.
        var source = fromModel ? SkillSource.AiExtracted : SkillSource.Parsed;

        foreach (var name in deduped)
        {
            if (!existing.TryGetValue(name, out var skill))
            {
                // Added explicitly: Skill.Id is client-generated, so EF reads the
                // set key as "already exists" and skips the INSERT unless told.
                skill = new Skill { Name = name };
                _db.Skills.Add(skill);
                existing[name] = skill;
            }

            resume.ResumeSkills.Add(new ResumeSkill { SkillId = skill.Id, Source = source });
        }

        return deduped.Count;
    }

    private async Task<SliceResult<CommitResponse>> CommitPostingAsync(
        DocumentImport import, PostingDraft? draft, CancellationToken ct)
    {
        if (draft is null)
            return SliceResult<CommitResponse>.Invalid(
                "This import has no posting draft to commit. Re-parse it or fill the draft in first.");

        // Company and title are not validated here. CreateApplicationHandler
        // enforces them, and duplicating the check would be the start of the two
        // implementations of one rule that architecture.md A4 is about — the
        // error text below would drift from the REST endpoint's within a phase.
        var created = await _createApplication.HandleAsync(
            new CreateApplicationRequest(
                draft.Company,
                draft.Title,
                draft.Location,
                // The full extracted text as the description, so the Phase 4
                // analyzer re-reads the original advertisement rather than a
                // paraphrase of it.
                draft.Description ?? import.ExtractedText,
                draft.SourceUrl,
                Notes: null,
                ResumeId: null),
            ct);

        if (created.Status != ResultStatus.Ok)
            return SliceResult<CommitResponse>.Invalid(created.Error!);

        var application = created.Value!;

        // Through the contract, because posting_skills is Applications-owned and
        // this is a write. Marked AiExtracted for the same reason Phase 4 marks
        // its own: a human who later types a skill by hand outranks it, and
        // AddExtractedSkillsAsync already refuses to restamp an existing row.
        var linked = await _postings.AddExtractedSkillsAsync(
            application.Posting.Id,
            draft.Skills.Select(s => new ExtractedSkill(s.Name, s.Required)).ToList(),
            ct);

        // Requirements go one at a time through the Applications slice, which is
        // the honest cost of reusing its use case instead of writing the table.
        // At the volume a job ad produces — a dozen bullets — a round trip each
        // is unnoticeable, and the alternative is a third method on a contract
        // that is explicitly capped at two.
        var requirements = 0;
        foreach (var requirement in draft.Requirements)
        {
            var result = await _addRequirement.HandleAsync(
                application.Id,
                new AddRequirementToPostingRequest(requirement.Text, requirement.Kind, requirement.IsMustHave),
                ct);
            if (result.Status == ResultStatus.Ok) requirements++;
        }

        import.Status = ImportStatus.Committed;
        import.CommittedAtUtc = DateTime.UtcNow;
        import.UpdatedAtUtc = import.CommittedAtUtc.Value;
        import.CommittedEntityId = application.Id;
        await _db.SaveChangesAsync(ct);

        return SliceResult<CommitResponse>.Ok(new CommitResponse(
            import.Id,
            import.Kind,
            application.Id,
            $"Logged an application for {draft.Title} at {draft.Company}.",
            linked,
            0,
            0,
            requirements));
    }
}
