namespace Jobkeep.SharedKernel;

// Phase 7. The C# half of the case-insensitive natural key.
//
// Three tables dedup on a human-typed name — `companies.Name`, `skills.Name`,
// `resumes.Label` — and until this phase all three did it case-sensitively, so
// "C#" and "c#" were two rows in the table whose entire purpose is
// deduplication. That cost something measurable: a duplicate split one skill's
// count in `/stats/skill-demand`, and the Phase 5 match check matches skill
// *rows*, so a difference of case read as a missing skill on a real CV.
//
// The fix has two halves that must agree:
//
//   * In the database — a STORED generated column (`NameNormalized`,
//     `LabelNormalized`) computed as `lower("Name")`, carrying the unique index.
//     Postgres maintains it, so no writer can forget it.
//   * In C# — this function, used by every find-or-create before it inserts.
//
// WHY THIS LIVES IN Shared/ AND NOT IN A MODULE
// ----------------------------------------------
// It started as a method on `CompanyLookup`, which put it in the Applications
// module — and then Documents needed it too (`AddSkillToResume`, `CommitImport`),
// which would have meant one module reaching into another's internals for a
// string function. Decision 17 says a module may *read* another's tables freely,
// but this is not a read; it is a shared rule, and a shared rule that two
// modules must apply identically belongs beside `SliceResult`, not inside one of
// them. Same reasoning that moved `IChatClient` out of the Ai module in Phase
// 4.5: the module owns its tables, not the technique.
//
// THE INVARIANT, AND WHAT BREAKS IF IT SLIPS
// -------------------------------------------
// This must produce exactly what `lower()` produces in Postgres. If the two ever
// disagree, a lookup misses a row that the unique index will then refuse to let
// it insert, and the user gets a 500 on a name that looks perfectly ordinary.
// `ToLowerInvariant` rather than `ToLower` because the database is not applying
// the server's culture either — a Turkish locale lowercasing "I" to "ı" on one
// side and not the other is precisely the drift this comment exists to prevent.
//
// Not covered, deliberately: Unicode normalisation and trimming. Postgres
// `lower()` does neither, so neither does this. Making C# smarter than the
// column is how the halves come apart.
public static class NaturalKey
{
    /// <summary>
    /// Normalise a human-typed name to its natural key — the value the unique
    /// index is actually built on. Mirrors <c>lower()</c> in Postgres.
    /// </summary>
    public static string Of(string name) => name.ToLowerInvariant();
}
