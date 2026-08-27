using Jobkeep.Modules.Ai;
using Jobkeep.Modules.Applications;
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
}
