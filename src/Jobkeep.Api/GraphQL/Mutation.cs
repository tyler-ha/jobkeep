using Jobkeep.Modules.Ai;
using Jobkeep.Modules.Applications;
using Jobkeep.Modules.Ats;
using Jobkeep.Modules.Documents;

namespace Jobkeep.GraphQL;

// GraphQL write side. Every mutation is a thin adapter: it hands the request to
// the same slice handler REST calls and translates the outcome.
//
// As of Phase 2.3 that is true of all seven, with no exceptions left. Until this
// phase, create/update/delete went straight to IJobApplicationRepository and
// skipped the validation the REST endpoints did by hand — which is why
// `createApplication(input: { company: "Canva", title: "" })` used to succeed
// against a POST that returned 400 for the same input (architecture.md A4). The
// rule now lives in CreateApplicationHandler, so there is nowhere for the two
// surfaces to disagree.
public class Mutation
{
    public async Task<ApplicationDetail> CreateApplication(
        CreateApplicationRequest input,
        [Service] CreateApplicationHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(input, ct)).ValueOrThrow();

    public async Task<ApplicationDetail> UpdateApplication(
        Guid id, UpdateApplicationRequest input,
        [Service] UpdateApplicationHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(id, input, ct)).ValueOrThrow();

    // Deleting an application that is already gone is now an error carrying
    // NOT_FOUND, where it used to return false. GraphQL has no status codes, so
    // `false` was the only signal available and it read identically to a
    // successful no-op — the REST side has always answered 404.
    public async Task<bool> DeleteApplication(
        Guid id, [Service] DeleteApplicationHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(id, ct)).ValueOrThrow();

    // PHASE 13.3c — the first way to delete a job ad, and the publisher behind
    // the delete notification that replaced `ai_analyses.PostingId`'s cascade.
    //
    // Takes a posting id rather than an application id, which is the odd one out
    // among these mutations and is correct: the ad outlives every application
    // logged against it, so addressing it through one of them would be naming a
    // row that has to be gone before this can succeed. The id is on every
    // application detail response as `posting.id`.
    public async Task<bool> DeletePosting(
        Guid id, [Service] DeletePostingHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(id, ct)).ValueOrThrow();

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

    // Phase 4 — runs the local model over the posting's description and stores
    // what it extracted. A mutation, not a query, on both surfaces: it writes an
    // ai_analyses row and posting_skills rows.
    //
    // This is the first field on either surface whose cost is measured in seconds
    // rather than milliseconds, and the first that can fail because something
    // outside the process is not running. Neither changes the adapter — the rule
    // about what a valid analysis is lives in the handler, so GraphQL and REST
    // cannot disagree about it, which is the same reason every field above is
    // three lines long.
    public async Task<AiAnalysisResponse> AnalyzePosting(
        Guid applicationId,
        [Service] AnalyzePostingHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(applicationId, ct)).ValueOrThrow();

    // Phase 4.5 — the three writes in the review cycle. The upload that starts it
    // is REST-only; see DocumentsModule.cs for why a file does not belong in a
    // GraphQL schema.
    //
    // HotChocolate publishes the draft record as `ImportDraft` on the way out and
    // `ImportDraftInput` on the way in, from the same CLR type — which is what
    // keeps "the shape you reviewed" and "the shape you send back corrected"
    // provably identical rather than two records that drift.
    public async Task<ImportResponse> ReviewImport(
        Guid id, ImportDraft draft,
        [Service] ReviewImportHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(id, draft, ct)).ValueOrThrow();

    // Re-runs the model over the stored text. The dividend of storing the
    // extracted text between the two stages: a better prompt or a better model
    // costs no re-upload (RestructureImport.cs).
    public async Task<ImportResponse> RestructureImport(
        Guid id,
        [Service] RestructureImportHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(id, ct)).ValueOrThrow();

    // The gate. Nothing an uploaded document proposes becomes a real row until
    // this is called.
    public async Task<CommitResponse> ConfirmImport(
        Guid id,
        [Service] CommitImportHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(id, ct)).ValueOrThrow();

    public async Task<bool> DiscardImport(
        Guid id,
        [Service] DiscardImportHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(id, ct)).ValueOrThrow();

    // PHASE 13.3c — delete a resume version. Errors carrying INVALID_INPUT when
    // an application or a stored ATS check still points at it, which is the same
    // refusal REST answers 400 to; the rule is in DeleteResumeHandler, so the two
    // surfaces have nowhere to disagree about it.
    public async Task<bool> DeleteResume(
        Guid id, [Service] DeleteResumeHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(id, ct)).ValueOrThrow();

    // The resume-side mirror of addSkillToPosting, and the first write to
    // `resume_skills` that is not part of the import cycle. Both halves of the
    // shared-skills join are now editable by hand on both surfaces, which is what
    // lets a user correct an ATS near-miss (the resume that says PostgreSQL in
    // prose and SQL in its skill list) without re-importing the document.
    public async Task<ResumeSkillResponse> AddSkillToResume(
        Guid resumeId, AddSkillToResumeRequest input,
        [Service] AddSkillToResumeHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(resumeId, input, ct)).ValueOrThrow();

    // Phase 6 step 6.1 — the inverse, added when the front-end design turned
    // "add a skill" into a drag. Unlinks the resume_skills join row; the shared
    // `skills` row survives, exactly as on the posting side.
    public async Task<bool> RemoveSkillFromResume(
        Guid resumeId, string skillName,
        [Service] RemoveSkillFromResumeHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(resumeId, skillName, ct)).ValueOrThrow();

    // Phase 5 — the ATS check. A mutation because it writes an ats_results row,
    // even though most of what it does is read.
    //
    // `resumeId` is nullable on both surfaces and means the same thing on both:
    // omit it to check against the resume the application was sent with. That is
    // the handler's rule, not this adapter's — the surfaces cannot disagree about
    // it because neither of them decides it.
    public async Task<AtsCheckResponse> CheckAts(
        Guid applicationId, Guid? resumeId,
        [Service] CheckAtsHandler handler, CancellationToken ct)
        => (await handler.HandleAsync(applicationId, resumeId, ct)).ValueOrThrow();
}
