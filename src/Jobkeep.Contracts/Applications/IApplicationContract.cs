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
// it" -- it has a write. This one answers "does this application exist, and what
// does it point at", and is read-only for now.
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
}
