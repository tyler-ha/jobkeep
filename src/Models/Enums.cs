namespace Jobkeep.Models;

// Stored as strings in Postgres (see AppDbContext) so rows stay
// self-documenting when you eyeball the table in psql.

public enum ApplicationStatus { Applied, Interviewing, Offer, Rejected, Withdrawn }

public enum EmploymentType { FullTime, PartTime, Contract, Casual, Internship }

public enum SalaryPeriod { Hour, Day, Month, Year }

public enum RequirementKind { Qualification, Responsibility, Benefit }

public enum SeniorityLevel { Unknown, Junior, Mid, Senior, Lead, Principal }

// Did a human enter this skill, or did the Phase 4 AI analyzer extract it?
public enum SkillSource { Parsed, AiExtracted }
