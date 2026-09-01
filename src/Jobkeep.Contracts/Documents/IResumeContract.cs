namespace Jobkeep.Modules.Documents;

// PHASE 13.2d: the interface and its DTO live in Jobkeep.Contracts; the
// implementation lives with the module that owns the table. The namespace is
// deliberately Jobkeep.Modules.Documents -- 13.6 renames namespaces to match
// projects, in one pass, once nothing else is moving.

// What another module is allowed to know about a résumé. Two fields, and the
// omissions are the point: no SourceText, no email, no phone, no location.
//
// A résumé is the most personal thing in this database and the security audit
// records it as such. Applications needs to answer exactly two questions about
// one — "does this id exist" and "what is it called" — and a contract that
// handed over the entity would have handed over a person's CV to satisfy a
// foreign-key check. Deliberately not the Resume entity, for the same reason
// SkillInfo is not the Skill entity, plus this one.
public record ResumeRef(Guid Id, string Label);

// What Documents exposes about a résumé.
//
// ---------------------------------------------------------------------------
// One method, and why the two questions did not become two
// ---------------------------------------------------------------------------
// The callers want different things. CreateApplication and UpdateApplication
// want existence, to refuse a stale id with a sentence instead of a foreign-key
// violation. ApplicationDetail wants the label, so a client can render
// "backend-focused" beside an application without a second round trip.
//
// Both are the same row and the same query, and the label is one short column.
// Splitting them would be one method per caller's question, which is the shape
// IJobApplicationRepository died of (decision 5) — and it would buy nothing,
// because the "cheaper" existence check would still be a primary-key lookup.
// Null means no such résumé, so existence is `is not null` at the call site and
// the caller writes its own message; that convention is the one every contract
// in this project already uses.
//
// A third method here would be worth stopping over. Applications has no business
// knowing anything else about a résumé, and if it starts wanting to, the
// question is whether the feature is in the right module.
public interface IResumeContract
{
    Task<ResumeRef?> GetAsync(Guid resumeId, CancellationToken ct = default);
}
