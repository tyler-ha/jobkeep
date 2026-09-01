using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// Slice: attach a skill to a resume, reusing the shared `skills` row when the
// name already exists (find-or-create by name).
//
// ---------------------------------------------------------------------------
// Why this exists, and why it exists *now*
// ---------------------------------------------------------------------------
// It is the deliberate mirror of Modules/Applications/AddSkillToPosting.cs, and
// until this slice there was no mirror: `posting_skills` could be edited by hand
// on both surfaces, while `resume_skills` could only ever be written by the
// Phase 4.5 import cycle. So the two halves of the shared-skills join were not
// symmetric, and the asymmetry had a cost the Phase 5 verification actually paid.
//
// That run reported `PostgreSQL` as a missing skill against a CV that names
// PostgreSQL in prose, because the resume's structured skill list said `SQL`. The
// gap is a set difference over skill ROWS, not over resume text (see
// Modules/Ats/CheckAts.cs), so the honest fix for a near-miss like that is to let
// the user say "yes, I have this" and have it land as a row. Without this slice
// the only way to correct it was to re-import the whole document.
//
// It is also what backs the CV-centre drag in the Phase 6 front end: dragging a
// missing skill from the ATS check onto your resume is exactly this call.
//
// ---------------------------------------------------------------------------
// The boundary
// ---------------------------------------------------------------------------
// This is a Documents slice because Documents owns `resume_skills` — the same
// split CommitImport.cs states: `skills` is the shared vocabulary table that
// belongs to no module, while each LINK table is module-owned. Applications owns
// `posting_skills`, Documents owns `resume_skills`, and neither writes the
// other's. That is why this is a near-copy of AddSkillToPosting rather than a
// shared helper the two call: sharing it would put one module's write path
// inside the other's file, which is the thing rule 2 exists to prevent.

public record AddSkillToResumeRequest(string SkillName, string? Category);

// A response DTO, not the EF entity (architecture.md A2). There is no IsRequired
// on the way in or the way out, and the absence is the same one Models/Resume.cs
// argues for: "required" is a property of what a posting asks for, not of what
// you have.
public record ResumeSkillResponse(string SkillName, string? Category, SkillSource Source);

public class AddSkillToResumeHandler
{
    private readonly AppDbContext _db;

    public AddSkillToResumeHandler(AppDbContext db) => _db = db;

    public async Task<SliceResult<ResumeSkillResponse>> HandleAsync(
        Guid resumeId, AddSkillToResumeRequest request, CancellationToken ct = default)
    {
        // Validation in the handler, not at either edge, so REST and GraphQL
        // cannot enforce different rules (architecture.md A4).
        var skillName = request.SkillName?.Trim();
        if (string.IsNullOrEmpty(skillName))
            return SliceResult<ResumeSkillResponse>.Invalid("skillName is required.");

        // The import path clips skill names to 100 characters before they reach
        // the database (CommitImport.Clip); a hand-typed one has to be held to the
        // same limit or the column length becomes the error message. Refusing is
        // better than silently truncating here: a 100-character "skill" is a
        // paste accident, and quietly storing the first 100 characters of one
        // creates a junk row in the table whose entire job is deduplication.
        if (skillName.Length > 100)
            return SliceResult<ResumeSkillResponse>.Invalid("skillName must be 100 characters or fewer.");

        // Same argument, and stated once for both: the column is 50, and letting a
        // longer value through turns a user's typo into a DbUpdateException from
        // three frames deeper with the column name in it.
        var category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim();
        if (category is { Length: > 50 })
            return SliceResult<ResumeSkillResponse>.Invalid("category must be 50 characters or fewer.");

        // Existence check projected to the key, not a load of the aggregate: this
        // slice writes one join row and has no use for the resume's text, its
        // experiences or its education.
        var exists = await _db.Resumes.AnyAsync(r => r.Id == resumeId, ct);
        if (!exists)
            return SliceResult<ResumeSkillResponse>.NotFound($"Resume {resumeId} not found.");

        // Reuse the shared skill row if one exists. This is the whole reason the
        // table is shared: the row this finds is the same row the posting side
        // links to, which is what makes the ATS check's gap a join rather than a
        // string comparison across two tables.
        //
        // The match is case-SENSITIVE, and that is the known dedup gap recorded in
        // CLAUDE.md, not an oversight in this slice. `C#` and `c#` become two rows
        // here exactly as they do in AddSkillToPosting and in the import path.
        // Fixing it means a case-insensitive natural key on `skills` — a
        // migration, and its own phase — and fixing it in one of the three writers
        // would leave them disagreeing about what a duplicate is.
        // Phase 7 — the case-insensitive natural key, same as the posting path.
        var key = NaturalKey.Of(skillName);
        var skill = await _db.Skills.FirstOrDefaultAsync(s => s.NameNormalized == key, ct);
        if (skill is null)
        {
            // Add explicitly: Skill.Id is client-generated in the property
            // initializer, so EF would otherwise read the set key as "already
            // exists", skip the INSERT and break the resume_skills FK.
            skill = new Skill { Name = skillName, Category = category };
            _db.Skills.Add(skill);
        }

        var link = await _db.ResumeSkills
            .FirstOrDefaultAsync(rs => rs.ResumeId == resumeId && rs.SkillId == skill.Id, ct);
        if (link is null)
        {
            link = new ResumeSkill
            {
                ResumeId = resumeId,
                SkillId = skill.Id,
                // Parsed, always. The enum's meaning on this table (Models/Resume.cs)
                // is Parsed = "you typed or corrected it", AiExtracted = "the
                // structuring step proposed it and you confirmed it unchanged".
                // Nothing reaches this handler except a human asking for it.
                Source = SkillSource.Parsed,
            };
            _db.ResumeSkills.Add(link);
            await _db.SaveChangesAsync(ct);
        }
        else if (link.Source == SkillSource.AiExtracted)
        {
            // A human confirming a skill the model proposed outranks the model.
            // The mirror of what IPostingContract.AddExtractedSkillsAsync refuses
            // to do in the other direction: the model never restamps a human's
            // row, and a human always restamps the model's.
            link.Source = SkillSource.Parsed;
            await _db.SaveChangesAsync(ct);
        }
        // else the pair is already linked, by a human, and asking again is a no-op
        // rather than an error — the composite PK on resume_skills makes "at most
        // once per resume" the schema's rule, so a client retrying is asking for a
        // state that already holds. 200, not 400. Same call as AddSkillToPosting.

        return SliceResult<ResumeSkillResponse>.Ok(
            new ResumeSkillResponse(skill.Name, skill.Category, link.Source));
    }
}
