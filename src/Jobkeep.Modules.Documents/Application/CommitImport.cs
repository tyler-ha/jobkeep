using Jobkeep.Contracts.Applications;
using Jobkeep.Contracts.Shared;
using Jobkeep.Contracts.Skills;
using Jobkeep.Modules.Documents.Domain;
using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Documents;

// Slice: the user confirms the draft, and it becomes real rows.
//
// ---------------------------------------------------------------------------
// The two halves commit through different machinery, on purpose
// ---------------------------------------------------------------------------
// A resume commits by writing this module's own tables. A job posting commits by
// asking the Applications module to do it. That asymmetry is the interesting
// design decision in this file, and it follows from who owns what:
//
//   * `resumes` and its children are Documents-owned. Nobody else writes them.
//   * `job_applications`, `job_postings` and `job_requirements` are
//     Applications-owned, and creating one has RULES — company and title are
//     required, the company name is resolved against a unique index rather than
//     inserted blind. Those rules live in Applications' own use cases.
//
// So the posting half hands the whole confirmed draft over rather than
// reimplementing any of it. The validation runs, the company dedup runs, and
// there is exactly one implementation of "what it means to create an
// application", which is the property architecture.md A4 was written to protect.
//
// ---------------------------------------------------------------------------
// PHASE 13.2c — what changed, and why the file got harder
// ---------------------------------------------------------------------------
// Until 13.2c this file did that by calling CreateApplicationHandler and
// AddRequirementToPostingHandler DIRECTLY, across a project reference that
// architecture.md decision 15 accepted openly as temporary. Both are gone. What
// crosses the boundary now is one call to IApplicationContract.CommitPostingAsync
// and one call to ISkillCatalog, and Jobkeep.Modules.Documents.csproj no longer
// references Jobkeep.Modules.Applications at all.
//
// The honest cost is that this file lost its transaction, and the paragraphs
// below in CommitPostingAsync are the replacement. It is worth being precise
// about why the transaction had to go rather than being kept "until it breaks":
// a database transaction can only span writes on one connection, and at 13.3 the
// other half of this operation is a different schema behind a different service.
// Keeping the transaction now would mean writing the failure handling later, in
// the step that is also moving tables — which is the exact mistake 13.1's own
// deviation note records.

public record CommitResponse(
    Guid ImportId,
    DocumentKind Kind,
    Guid CommittedEntityId,
    string Description,
    int SkillsLinked,
    int ExperiencesCreated,
    int EducationsCreated,
    int RequirementsCreated);

public record CommitImport(Guid Id) : IRequest<SliceResult<CommitResponse>>;

public class CommitImportHandler : IRequestHandler<CommitImport, SliceResult<CommitResponse>>
{
    private readonly DocumentsDbContext _db;
    private readonly ISkillCatalog _skills;
    private readonly IApplicationContract _applications;

    public CommitImportHandler(
        DocumentsDbContext db,
        ISkillCatalog skills,
        IApplicationContract applications)
    {
        _db = db;
        _skills = skills;
        _applications = applications;
    }

    public async ValueTask<SliceResult<CommitResponse>> Handle(
        CommitImport message, CancellationToken ct)
    {
        var id = message.Id;
        var import = await _db.DocumentImports.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (import is null)
            return SliceResult<CommitResponse>.NotFound($"Import {id} not found.");

        // Committing twice would create a second resume from the same draft. The
        // status check is what makes the confirm button safe to double-click,
        // which is not a hypothetical on a request that takes seconds.
        //
        // CommitFailed is admitted alongside AwaitingReview because it is the
        // state that MEANS "try again" — see ImportStatus, and see the recovery
        // path in CommitPostingAsync, which reads CommittedEntityId to decide
        // whether a retry starts over or only finishes.
        // Parsing says "not yet", not "already" — see ReviewImport for the same
        // distinction. Confirming a draft the model has not written yet would
        // commit the empty placeholder.
        if (import.Status == ImportStatus.Parsing)
            return SliceResult<CommitResponse>.Invalid(
                "This import is still being read. Wait for the draft before confirming it.");

        if (import.Status is not (ImportStatus.AwaitingReview or ImportStatus.CommitFailed))
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

        // The label is the one field on this screen the user typed themselves, so
        // it is validated rather than clipped: silently shortening a name someone
        // chose is worse than telling them it is too long, and the uniqueness rule
        // below means a clipped label could collide with an existing one. Every
        // other field here comes from the model and is clipped instead — see Clip.
        if (label.Length > DraftLimits.MaxLabelLength)
            return SliceResult<CommitResponse>.Invalid(
                $"That label is {label.Length} characters. Keep it under {DraftLimits.MaxLabelLength}.");

        // Checked before the insert rather than caught after, so the user gets a
        // sentence instead of a unique-index violation. Same pattern as
        // CompanyLookup.
        // Phase 7 — the conflict check must ask the same question the unique
        // index does, or the user is told the label is free and then gets a 500.
        var labelKey = NaturalKey.Of(label);
        if (await _db.Resumes.AnyAsync(r => r.LabelNormalized == labelKey, ct))
            return SliceResult<CommitResponse>.Invalid(
                $"A resume labelled \"{label}\" already exists. Pick a different label.");

        // ------------------------------------------------------------------
        // Skills FIRST, before anything is added to the change tracker
        // ------------------------------------------------------------------
        // 13.2c: find-or-create moved behind ISkillCatalog, which does its own
        // SaveChanges (its interface says so at length, and explains why it has
        // to once Skills is a service).
        //
        // 13.3b MADE THAT LITERAL. The catalog now saves through its own
        // own unit of work, on the `skills` schema, in a
        // transaction this method has no part in. What was "the same context, so
        // mind the flush" is now simply two transactions.
        //
        // Resolving skills before building the resume is what keeps that
        // harmless, and the argument is unchanged by the split. Do it the other
        // way round and the catalog's save would commit a half-built resume -
        // no skills, no import status change — in its own transaction, and a
        // failure just after would leave a resume the user cannot re-import (the
        // label check above would refuse the retry). Ordering is the whole fix;
        // there is no cleverer one available without a distributed transaction.
        var resolved = await ResolveSkillsAsync(draft.Skills, draft.SoftSkills, ct);

        var resume = new Resume
        {
            Label = label,
            FullName = Clip(draft.FullName, 200),
            Email = Clip(draft.Email, 320),
            Phone = Clip(draft.Phone, 50),
            Location = Clip(draft.Location, 200),
            Headline = draft.Headline,          // text, no column cap
            // The verbatim extracted text, not the draft. Phase 5's match check
            // reads this: comparing a posting against the structured summary
            // would compare it against what the model chose to keep, which is
            // exactly the layer this phase exists to make optional.
            SourceText = import.ExtractedText,
            SourceFileName = import.FileName,
            SourceHash = import.ContentHash,
            // Phase 5 reads this to decide whether to warn about PDF layout.
            // It is the format extraction DETECTED from the bytes, so a .docx
            // renamed to .pdf does not trigger a PDF warning.
            SourceFormat = import.Format
        };

        var ordinal = 0;
        foreach (var experience in draft.Experience)
        {
            resume.Experiences.Add(new ResumeExperience
            {
                Employer = Clip(experience.Employer.Trim(), 200)!,
                Title = Clip(experience.Title, 200),
                StartText = Clip(experience.Start, 50),
                EndText = Clip(experience.End, 50),
                Highlights = experience.Highlights,
                Ordinal = ordinal++
            });
        }

        ordinal = 0;
        foreach (var education in draft.Education)
        {
            resume.Educations.Add(new ResumeEducation
            {
                Institution = Clip(education.Institution.Trim(), 200)!,
                Qualification = Clip(education.Qualification, 200),
                YearText = Clip(education.Year, 50),
                Ordinal = ordinal++
            });
        }

        // Provenance follows where the content came from, not who approved it.
        // A draft the model wrote and the user confirmed unchanged is still
        // AiExtracted; a draft the user edited cleared ModelUsed (ReviewImport)
        // and is Parsed. Confirming is not authorship.
        var source = import.ModelUsed is not null ? SkillSource.AiExtracted : SkillSource.Parsed;

        foreach (var skill in resolved)
            resume.ResumeSkills.Add(new ResumeSkill { SkillId = skill.Id, Source = source });

        _db.Resumes.Add(resume);

        import.Status = ImportStatus.Committed;
        import.CommittedAtUtc = DateTime.UtcNow;
        import.UpdatedAtUtc = import.CommittedAtUtc.Value;
        import.CommittedEntityId = resume.Id;

        // One SaveChanges for the resume, its children, the links and the
        // import's status change. They are one user action, so they are one
        // transaction: a commit that half-applied would leave a resume with no
        // skills and an import that still says it needs reviewing.
        //
        // The `skills` rows themselves are no longer inside it — see
        // ResolveSkillsAsync. That is the one atomicity 13.2c gave up.
        await _db.SaveChangesAsync(ct);

        return SliceResult<CommitResponse>.Ok(new CommitResponse(
            import.Id,
            import.Kind,
            resume.Id,
            $"Saved resume \"{resume.Label}\".",
            resolved.Count,
            resume.Experiences.Count,
            resume.Educations.Count,
            0));
    }

    // Trim model output to what the column will hold.
    //
    // Clipping rather than refusing, and the asymmetry with the label check above
    // is the decision worth defending. A model asked to copy a job title out of a
    // resume will occasionally return the whole line it found it on; the column is
    // varchar(200) and Postgres answers that with 22001, which surfaces as an
    // unhandled DbUpdateException — a 500 for a document that parsed fine. Nothing
    // the user did caused it and nothing they can do fixes it, because the offending
    // value came from the model. So the over-long field is shortened and the import
    // still commits, which is the same call the review screen already embodies:
    // imperfect structure the user can correct beats a hard failure.
    private static string? Clip(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max].TrimEnd();

    // Turn a draft's skill names into the shared rows they name.
    //
    // ---------------------------------------------------------------------
    // What 13.2c took out of here, and what it left
    // ---------------------------------------------------------------------
    // This used to be forty lines of find-or-create against `skills`, written
    // here because "skills is the shared vocabulary table that belongs to no
    // module". That claim was true and is exactly why it had to move: a table
    // four modules write is a table with four chances to get its natural key
    // wrong, and Phase 7 made getting it wrong a 500 on an ordinary name.
    // `Jobkeep.Modules.Skills` owns it now, and this method is what is left —
    // clipping, which is a Documents rule about model output, and nothing else.
    //
    // Dedup is no longer done here either. The catalog collapses spellings onto
    // one row and returns a dictionary that may map two keys to one SkillInfo,
    // so DistinctBy on the id is what turns its answer back into a set of links.
    // The visible consequence is unchanged: an import naming "C#" and "c#"
    // creates one link, and the first spelling in the document is the one stored.
    // PHASE 14 — takes the two draft lists rather than one, because a résumé's
    // technical and soft skills arrive separately (see ResumeExtraction) and the
    // only difference between them is the Kind they carry into the catalogue.
    //
    // ONE call, not two, and that matters: FindOrCreateAsync saves, so two calls
    // would be two transactions and a failure between them would leave half a
    // vocabulary committed. Merging the lists here keeps the ordering rule this
    // file already obeys — resolve everything, then add rows of our own.
    private async Task<IReadOnlyList<SkillInfo>> ResolveSkillsAsync(
        List<string> technical, List<string> soft, CancellationToken ct)
    {
        // Clipped to the column width for the same reason every other field here
        // is: the name came from a model, and a name longer than the column is a
        // 500 the user cannot act on.
        var requested = technical
            .Select(n => (Name: n, Kind: SkillKind.Technical))
            .Concat(soft.Select(n => (Name: n, Kind: SkillKind.Soft)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new SkillRequest(Clip(x.Name.Trim(), 100)!, Kind: x.Kind))
            .ToList();

        if (requested.Count == 0) return [];

        var resolved = await _skills.FindOrCreateAsync(requested, ct);

        // DistinctBy the id, not the name: resume_skills has a composite primary
        // key on (ResumeId, SkillId), so two spellings resolving to one row would
        // be a duplicate-key exception on SaveChanges rather than a no-op.
        return resolved.Values.DistinctBy(s => s.Id).ToList();
    }

    private async Task<SliceResult<CommitResponse>> CommitPostingAsync(
        DocumentImport import, PostingDraft? draft, CancellationToken ct)
    {
        if (draft is null)
            return SliceResult<CommitResponse>.Invalid(
                "This import has no posting draft to commit. Re-parse it or fill the draft in first.");

        // Company and title are not validated here. Applications enforces them,
        // and duplicating the check would be the start of the two implementations
        // of one rule that architecture.md A4 is about — the error text below
        // would drift from the REST endpoint's within a phase.

        // ------------------------------------------------------------------
        // PHASE 13.2c — this used to be one transaction, and now it is a protocol
        // ------------------------------------------------------------------
        // The resume path above is atomic for free: it builds an object graph and
        // calls SaveChanges once. This one never could be, because it saves on
        // both sides of a module boundary — and until 13.2c it papered over that
        // with `_db.Database.BeginTransactionAsync`, which worked only because
        // both sides happened to share a connection.
        //
        // The failure that transaction was protecting against is worth restating,
        // because the replacement has to answer the same one. It is NOT a lost
        // write. It is a DUPLICATE one: if anything after the application insert
        // failed, the application and its company were already committed while
        // this import still read AwaitingReview — so the double-click guard in
        // Handle did not fire, and confirming again logged a SECOND
        // application for the same document. That is the worst outcome available
        // to a feature whose entire premise is that nothing exists until a human
        // confirms it.
        //
        // The replacement is a three-step protocol, and each step exists to
        // answer one question a crash leaves open:
        //
        //   1. CLAIM. Mark the import committed before calling out, so a second
        //      request arriving during the call is refused by the same guard.
        //      CommittedEntityId is still null, which is what "we started and do
        //      not yet know the outcome" looks like in this table.
        //   2. CALL. One contract call, deliberately — application, skills and
        //      requirements together (IApplicationContract says why). One call
        //      leaves one half-state to reason about; three would leave three.
        //   3. RECORD. Write the id back. From here the import is a receipt.
        //
        // And the recovery, which is what makes CommitFailed re-runnable rather
        // than merely honest: a retry that finds CommittedEntityId already set
        // knows the rows exist and only finishes step 3. A retry that finds it
        // null knows nothing was logged and starts over. That is the idempotency
        // guard, and it is a field the table already had.
        //
        // The accepted cost, stated plainly because it is a real regression from
        // the transaction: the window between the contract call returning and the
        // id being saved is not covered. A crash exactly there leaves an
        // application logged and an import that will duplicate it on retry. It is
        // one UPDATE on an already-loaded row immediately after a successful call
        // on the same connection, and the dominant cause of failure there —
        // a cancelled request — is closed below by not passing the token. What
        // remains is narrow, unavoidable without a distributed transaction, and
        // strictly smaller than the window Phases 4.5 through 13.2b shipped with
        // on the resume-then-skills path.

        // The recovery branch, checked before the claim so a resumed commit costs
        // one write rather than two. A previous attempt created the application
        // and did not finish, so this run must not create a second one.
        if (import.CommittedEntityId is { } already)
            return await FinishAsync(import, already, draft, ct);

        // Step 1 — CLAIM.
        import.Status = ImportStatus.Committed;
        import.CommittedAtUtc = DateTime.UtcNow;
        import.UpdatedAtUtc = import.CommittedAtUtc.Value;
        await _db.SaveChangesAsync(ct);

        PostingCommitResult result;
        try
        {
            // Step 2 — CALL.
            result = await _applications.CommitPostingAsync(
                new PostingCommitRequest(
                    // Clipped to the columns' widths for the same reason the resume
                    // fields are. Only the LENGTHS are handled here; whether a company
                    // or title is required at all stays Applications' rule, which is
                    // why this passes the values on rather than pre-checking them.
                    Clip(draft.Company, 200)!,
                    Clip(draft.Title, 300)!,
                    // Not clipped: job_postings.Location has no HasMaxLength, so it is
                    // `text`. Clipping a column that would have held the value is the
                    // same silent data loss this method exists to avoid, pointing the
                    // other way.
                    draft.Location,
                    // The full extracted text as the description, so the Phase 4
                    // analyzer re-reads the original advertisement rather than a
                    // paraphrase of it.
                    draft.Description ?? import.ExtractedText,
                    draft.SourceUrl,
                    draft.Skills.Select(s => new ExtractedSkill(Clip(s.Name, 100)!, s.Required, s.Kind)).ToList(),
                    draft.Requirements
                        .Select(r => new PostingRequirement(r.Text, r.Kind, r.IsMustHave))
                        .ToList()),
                ct);
        }
        catch
        {
            // Unknown outcome. The import is marked so the user can retry and so
            // the state is not silently indistinguishable from a finished commit.
            //
            // CancellationToken.None on purpose: the commonest way to arrive here
            // is a cancelled request, and passing the cancelled token would refuse
            // to write the very row that records what happened.
            import.Status = ImportStatus.CommitFailed;
            import.CommittedAtUtc = null;
            import.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        if (result.Error is not null && result.ApplicationId == Guid.Empty)
        {
            // REFUSED. A clean no-op on the other side of the boundary — the
            // contract guarantees nothing was created — so the claim is rewound
            // rather than left as CommitFailed. The user edits the draft and
            // confirms again, which is what AwaitingReview means.
            import.Status = ImportStatus.AwaitingReview;
            import.CommittedAtUtc = null;
            import.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return SliceResult<CommitResponse>.Invalid(result.Error);
        }

        if (result.Error is not null)
        {
            // INCOMPLETE. The application exists and the rest of it did not
            // finish. Record the id FIRST — that is the whole reason the contract
            // hands it back on a failure — and only then mark the import as
            // needing another run. Written in that order because the id is what
            // makes the retry safe, and CommitFailed without it is an invitation
            // to duplicate.
            import.CommittedEntityId = result.ApplicationId;
            import.Status = ImportStatus.CommitFailed;
            import.CommittedAtUtc = null;
            import.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);

            return SliceResult<CommitResponse>.Invalid(
                "The application was logged, but the rest of the import did not finish. "
                + $"Confirm it again to complete it. ({result.Error})");
        }

        // Step 3 — RECORD. See the window note above for why the token is dropped.
        import.CommittedEntityId = result.ApplicationId;
        await _db.SaveChangesAsync(CancellationToken.None);

        // Requirements the Applications module refused are counted and said out
        // loud. They used to be dropped silently, which left the user reading a
        // 200 OK whose RequirementsCreated was quietly smaller than the list they
        // had just confirmed on screen, with nothing naming what went missing.
        var note = result.RequirementsRejected == 0
            ? ""
            : $" {result.RequirementsRejected} requirement{(result.RequirementsRejected == 1 ? " was" : "s were")} rejected and not saved.";

        return SliceResult<CommitResponse>.Ok(new CommitResponse(
            import.Id,
            import.Kind,
            result.ApplicationId,
            $"Logged an application for {draft.Title} at {draft.Company}.{note}",
            result.SkillsLinked,
            0,
            0,
            result.RequirementsCreated));
    }

    // The tail of a commit that already created its application on an earlier
    // attempt. There is nothing left to write on the Applications side, so this
    // only closes the import out.
    //
    // The counts come back as zero rather than being re-derived, and that is
    // deliberate: they describe what THIS call did, and this call created
    // nothing. Asking Applications how many skills the posting ended up with
    // would be a new contract method answering a question Documents has about
    // someone else's feature, which is the test ISkillCatalog spells out and the
    // reason IPostingContract carries a cap.
    private async Task<SliceResult<CommitResponse>> FinishAsync(
        DocumentImport import, Guid applicationId, PostingDraft draft, CancellationToken ct)
    {
        import.Status = ImportStatus.Committed;
        import.CommittedAtUtc = DateTime.UtcNow;
        import.UpdatedAtUtc = import.CommittedAtUtc.Value;
        await _db.SaveChangesAsync(ct);

        return SliceResult<CommitResponse>.Ok(new CommitResponse(
            import.Id,
            import.Kind,
            applicationId,
            $"The application for {draft.Title} at {draft.Company} was already logged by an earlier attempt, "
            + "and this import is now closed out.",
            0, 0, 0, 0));
    }

}
