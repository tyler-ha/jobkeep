using Jobkeep.Contracts.Applications;
using Jobkeep.Contracts.Shared;
namespace Jobkeep.Modules.Applications.Domain;

// Stored as strings in Postgres (HasConversion<string> in each entity's
// configuration) so rows stay self-documenting when you eyeball the table in
// psql.
//
// PHASE 13.3b — this file used to hold nine enums for the whole application and
// now holds the three that describe a POSTING. The others went where their
// entity went: SeniorityLevel to Ai, DocumentKind / ImportStatus / SourceFormat
// to Documents (they were always beside DocumentImport), and ApplicationStatus
// and SkillSource to Jobkeep.Contracts, because those two are named by two
// modules' response DTOs each and a duplicated CLR enum breaks the GraphQL
// schema build. SharedEnums.cs argues that one at length.
//
// RequirementKind stays HERE and Documents no longer names it. Documents' draft
// DTOs used to carry this enum and map it to PostingRequirementKind on the way
// into the commit; since 13.3b the drafts carry the Contracts enum directly, so
// the mapping switch is gone. Same member names, so the REST payload and the
// stored DraftJson are byte-identical either way.

public enum EmploymentType { FullTime, PartTime, Contract, Casual, Internship }

public enum SalaryPeriod { Hour, Day, Month, Year }

public enum RequirementKind { Qualification, Responsibility, Benefit }
