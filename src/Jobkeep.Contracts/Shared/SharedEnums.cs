namespace Jobkeep.Models;

// ---------------------------------------------------------------------------
// PHASE 13.3b — the two enums that could not follow their entity
// ---------------------------------------------------------------------------
// Every other enum in this system moved into the module that owns the table it
// is stored in, and where one had to cross a boundary the pattern was a COPY in
// Contracts with an explicit mapping switch — PostingRequirementKind and
// ResumeSourceFormat both do exactly that, and the argument for copying is
// written out beside them.
//
// These two cannot use that pattern, and the reason is the GraphQL schema rather
// than taste. Both appear in the response DTOs of TWO modules:
//
//   ApplicationStatus — ApplicationDetail / ListApplications (Applications)
//                       and StatusCount inside ApplicationFunnel (Analytics).
//   SkillSource       — PostingSkillResponse (Applications) and
//                       ResumeSkillItem / ResumeSkillResponse (Documents).
//
// Both surfaces are published, so both CLR enums reach HotChocolate. Two enums
// with the same name in one schema is a schema-BUILD failure, not a warning:
// every GraphQL request would 500, and nothing in the C# build would say why.
// Renaming one copy to make room would put a type called `AnalyticsApplication
// Status` on the wire, which is a worse answer than sharing the real one.
//
// So they move here instead: one CLR type, one GraphQL enum, no mapping switch
// to keep in step. That is honest rather than a workaround — an application's
// status is the most published fact in this system, and where a skill came from
// is asked on both sides of the same join. Both are genuinely shared vocabulary,
// which is what this project is for.
//
// The namespace is Jobkeep.Models rather than a Contracts one, deliberately, and
// it reads oddly on purpose: the entities that carry these values keep that
// namespace through 13.3b too, so nothing here needs a using it did not already
// have. 13.6 renames every namespace to match its project in one pass; doing it
// early would do that step's job badly and bury this diff in churn.
//
// Stored as strings in Postgres (HasConversion<string> in each configuration)
// so rows stay self-documenting when you eyeball the table in psql.

public enum ApplicationStatus { Applied, Interviewing, Offer, Rejected, Withdrawn }

// Did a human enter this skill, or did the Phase 4 AI analyzer extract it?
public enum SkillSource { Parsed, AiExtracted }
