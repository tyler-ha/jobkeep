using Jobkeep.SharedKernel;

namespace Jobkeep.Modules.Match.Domain;

// Phase 5 output — 1:1 with an application. The keyword lists map to Postgres
// text[] columns via Npgsql (no child tables needed for simple string arrays).
//
// PHASE 13.3b — this class was declared in the same file as JobApplication,
// which is where it belonged while one project held every entity. It is Match'
// table, so it moved here, and both of its navigation properties went in the
// move: `match_results` is now its own schema and neither `job_applications` nor
// `resumes` is reachable from it.
public class MatchResult : IOwned
{
    // PHASE 11.2b — the owner. Stamped once, on insert, by
    // AuditSaveChangesInterceptor; never assigned by a slice, and never sent by
    // a client. Enforced on read by the `Owner` global query filter in
    // MatchDbContext. See IOwned for why the children do not carry it.
    public Guid OwnerUserId { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();

    // Which application this result judged. Still 1:1 — a unique index enforces
    // that, and re-checking overwrites, latest wins.
    //
    // 13.3b DROPPED THE FOREIGN KEY, so the CASCADE that used to delete this row
    // with its application is gone. Deleting an application therefore leaves an
    // orphan here until 13.3c adds the delete notification that replaces it.
    // Named rather than left implicit: an orphaned result is invisible (nothing
    // reads match_results except by application id), which is exactly the kind of
    // gap that gets found by a row count two phases later.
    public Guid ApplicationId { get; set; }

    // Which resume version this result judged. Added in Phase 5, because this
    // class predates Phase 4.5 and was written when a resume was a string column
    // on the application. Resumes are versioned and labelled now, so a result
    // that does not say which version it read is not a result — you would not
    // know whether the gaps it lists have already been fixed.
    //
    // Nullable only so existing rows survive the migration; every row this phase
    // writes sets it.
    //
    // 13.3b dropped this FK too, and GetMatchResult.cs already says what happens
    // then: the label comes back null rather than the read failing. That was a
    // documented impossibility while the RESTRICT held it shut, and it is
    // reachable now.
    public Guid? ResumeId { get; set; }

    // Three buckets, not two, and the split is the point: posting_skills.IsRequired
    // already distinguishes a must-have from a nice-to-have, so collapsing both
    // into one list would throw away information Phase 4 paid a model call to get.
    // "Missing a must-have" is a reason not to apply; "missing a nice-to-have" is
    // a line to add to the cover letter.
    public List<string> MatchedKeywords { get; set; } = new();
    public List<string> MissingMustHaveKeywords { get; set; } = new();
    public List<string> MissingNiceToHaveKeywords { get; set; } = new();

    // The model half's output: free-text requirements the resume shows no
    // evidence for. Empty when the model was unreachable — see RunMatchCheck.cs, which
    // degrades rather than failing, because three of its four stages need no model.
    public List<string> UnmetRequirements { get; set; } = new();

    public List<string> FormattingRiskNotes { get; set; } = new();

    // Something imperfect that did not stop the check — overwhelmingly "the model
    // was unreachable, so the free-text requirements were not assessed".
    //
    // Persisted rather than returned only from the POST, and the reason is the
    // failure mode DocumentImport.Warning was added to prevent: without it, a
    // result computed during an outage is stored with an empty UnmetRequirements
    // list, and every later read of it cheerfully reports that the resume meets
    // every written requirement. Same column, same argument, same table shape.
    public string? Warning { get; set; }

    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
}
