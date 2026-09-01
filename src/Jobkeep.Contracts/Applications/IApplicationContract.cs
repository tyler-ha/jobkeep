namespace Jobkeep.Modules.Applications;

// PHASE 13.2: the interface and its DTOs live in Jobkeep.Contracts; the
// implementation lives with the module that owns the tables. The namespace is
// deliberately unchanged -- 13.6 renames namespaces to match projects, in one
// pass, once nothing else is moving.

// What Applications exposes about an APPLICATION, as opposed to IPostingContract
// which is about the posting behind one.
//
// The two are kept apart rather than merged into one Applications-facade because
// they are the two halves a service split separates: 13.3 gives postings and
// applications the same schema, but the questions have different shapes.
// IPostingContract answers "what does this ad say, and record what you found in
// it". This one answers "does this application exist, and bring one into
// existence".
public interface IApplicationContract
{
    // Resolves an application id to the posting it points at. Null when the
    // application does not exist, so the caller writes its own NotFound message
    // -- the same convention IPostingContract.GetContentAsync uses, and for the
    // same reason: which id was wrong is the caller's sentence to write.
    //
    // Deliberately narrower than GetContentAsync, which returns the posting id
    // AND its whole description. A caller that only needs to resolve an id
    // should not pull a 20,000-character job ad over to discard it -- that is
    // finding A1, applied at a module boundary instead of at the API edge.
    Task<Guid?> GetPostingIdAsync(Guid applicationId, CancellationToken ct = default);

    // Creates an application, its posting, its skills and its requirements, in
    // one call.
    //
    // -----------------------------------------------------------------------
    // Why this is ONE method and not three
    // -----------------------------------------------------------------------
    // It replaces two direct handler calls Documents used to make across a
    // project reference (architecture.md decision 15, which accepted that
    // compile-time coupling openly until this phase). The obvious translation
    // would have been one contract method per handler — create the application,
    // then add the skills, then add each requirement — which is exactly the
    // shape that killed
    // IJobApplicationRepository: one method per thing the caller wanted to do
    // next, and no bound on the list.
    //
    // The shape that survives a service split is one method per *thing the
    // caller wants to have happened*. Documents does not want to create an
    // application and then separately want twelve requirements; it wants a
    // confirmed job-ad draft to become a logged application. That is one
    // intention, so it is one call — and it is the call that stays sensible when
    // it becomes an HTTP request, where twelve round trips for twelve bullet
    // points would be indefensible.
    //
    // It is also what makes the caller's failure handling writable. Documents has
    // to answer "did anything get created?" after a crash, and it can only answer
    // that about ONE call. Three calls give it three half-states to reason about
    // and no way to tell them apart.
    //
    // The rules still live in Applications. This contract carries no validation
    // of its own: company and title are checked by the handler behind it, which
    // is the property architecture.md A4 exists to protect, and the reason the
    // error text comes back through the result rather than being written twice.
    Task<PostingCommitResult> CommitPostingAsync(
        PostingCommitRequest request, CancellationToken ct = default);
}

// A job ad the caller has confirmed and wants logged.
//
// Deliberately not CreateApplicationRequest, which lives in the Applications
// module and carries fields (Notes, ResumeId) that only its own callers set. A
// contract DTO is the wire schema, so it holds what crosses the boundary and
// nothing that merely happens to be nearby.
public record PostingCommitRequest(
    string Company,
    string Title,
    string? Location,
    string? Description,
    string? SourceUrl,
    // The same ExtractedSkill IPostingContract takes, reused rather than mirrored:
    // it already means "a skill a machine found in an ad, with whether the ad said
    // it was required", which is exactly what a confirmed draft carries.
    IReadOnlyList<ExtractedSkill> Skills,
    IReadOnlyList<PostingRequirement> Requirements);

public record PostingRequirement(string Text, PostingRequirementKind Kind, bool IsMustHave);

// A copy of the RequirementKind enum, and the duplication is correct rather than
// tolerated. Jobkeep.Contracts must reference no other Jobkeep assembly — it
// becomes the wire schema when a module is extracted, and
// ModuleBoundaryTests.Foundation_projects_depend_on_nothing_of_ours enforces it
// — so it cannot name the entity enum, which lives with the entity in
// Jobkeep.Infrastructure.Data.
//
// At 13.3 these are two services that happen to agree on three words, which is
// what a shared vocabulary looks like once it has to cross a network. The
// mapping between them is an explicit switch in ApplicationContract rather than
// a cast, so adding a value to one without the other fails to compile instead of
// silently renumbering.
public enum PostingRequirementKind { Qualification, Responsibility, Benefit }

// What came of a commit.
//
// Not SliceResult<T>: that lives in SharedKernel, which Contracts may not
// reference for the reason above. The shape is deliberately flatter anyway — a
// commit either refused with a sentence, or happened and has counts.
public record PostingCommitResult(
    Guid ApplicationId,
    Guid PostingId,
    int SkillsLinked,
    int RequirementsCreated,
    // Requirements the Applications module refused. Counted and returned rather
    // than swallowed: the caller reports them to the user, because a 200 whose
    // saved count is quietly smaller than the list on screen is the failure this
    // number exists to prevent.
    int RequirementsRejected,
    // Non-null when the commit did not fully succeed. WHICH failure it was is
    // read off ApplicationId, and the caller must distinguish them because the
    // two need opposite handling:
    //
    //   * ApplicationId == Guid.Empty — REFUSED. A validation rule said no and
    //     nothing was created. Clean: show the sentence, let the user edit the
    //     draft and try again.
    //   * ApplicationId set — INCOMPLETE. The application exists; the skills or
    //     the requirements did not all land. The caller MUST record the id, or a
    //     retry logs the job twice.
    //
    // Returning the id on a failure rather than throwing is the whole reason this
    // shape exists. A contract that throws tells the caller only "something went
    // wrong", and the one thing the caller needs after a partial write is exactly
    // the thing an exception cannot carry: what got created.
    //
    // A failure BEFORE the application exists still throws, because there is
    // nothing to report and nothing for the caller to record.
    string? Error)
{
    public static PostingCommitResult Refused(string error) =>
        new(Guid.Empty, Guid.Empty, 0, 0, 0, error);

    public static PostingCommitResult Incomplete(Guid applicationId, Guid postingId, string error) =>
        new(applicationId, postingId, 0, 0, 0, error);

    public static PostingCommitResult Ok(
        Guid applicationId, Guid postingId, int linked, int created, int rejected) =>
        new(applicationId, postingId, linked, created, rejected, null);
}
