using Jobkeep.Data;
using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: the query surface. Filter by status / company / title / skill / applied
// date, choose a sort, page the results.
//
// This is the phase-2 thesis paying off. Postgres was chosen over DynamoDB
// (architecture.md decision 1) on the argument that a normalized model makes
// cross-cutting questions cheap. "Show me every application whose ad mentions
// C#" is the concrete case: one JOIN through posting_skills into the shared
// skills table, versus a full scan plus client-side filtering in a document
// store. The Skill filter below is that argument, executable.
//
// Filtering lives here, in the handler, rather than in either API surface,
// because a filter is a business rule and REST and GraphQL must not be able to
// enforce different ones. Projection — which fields a caller wants back — is a
// transport concern and is a separate question (see the note on A1 below).

public enum ApplicationSort { DateApplied, Company, Title, Status, UpdatedAt }

public enum SortDirection { Asc, Desc }

// One query object serving both surfaces: minimal APIs bind it from the query
// string with [AsParameters], HotChocolate publishes it as an input type.
//
// EVERY property is nullable, including the ones with an obvious default.
// [AsParameters] binds each property independently and treats a non-nullable
// value type as required — `?sort=` omitted would 400 with "Required parameter
// ApplicationSort Sort was not provided", and a property initializer does not
// change that. Nullable-plus-a-default-in-the-handler means "omitted" is
// expressible on both surfaces, and the defaults are applied in exactly one
// place instead of once per binder.
public record ApplicationQuery
{
    public ApplicationStatus? Status { get; init; }
    public string? Company { get; init; }
    public string? Title { get; init; }
    public string? Skill { get; init; }
    public DateOnly? AppliedFrom { get; init; }
    public DateOnly? AppliedTo { get; init; }
    public ApplicationSort? Sort { get; init; }
    public SortDirection? Direction { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

// Deliberately flat, and deliberately not ApplicationDetail: a list row shows
// what you scan a list for. Description, resume text, salary and requirements
// are detail-view fields, and shipping them in every list row is the over-fetch
// this phase set out to remove.
public record ApplicationListItem(
    Guid Id,
    string Company,
    string Title,
    string? Location,
    ApplicationStatus Status,
    DateOnly DateApplied,
    List<string> Skills);

// A concrete page type rather than a generic PagedResult<T>. HotChocolate names
// GraphQL types after the CLR type, and a generic would land in the schema as
// something like `ApplicationListItemPagedResult`. The cost: Phase 2.4 declares
// its own page type instead of reusing this one. At two call sites that is
// cheaper than a schema full of generated names.
public record ApplicationPage(
    List<ApplicationListItem> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages);

public class ListApplicationsHandler
{
    // A cap, not a preference. PageSize is caller-supplied and reaches Take()
    // directly; without a ceiling, `?pageSize=1000000` is a free denial of
    // service on an unauthenticated surface.
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 20;

    // Declared to Postgres as the ILIKE escape character; see Escape().
    private const string EscapeChar = @"\";

    private readonly AppDbContext _db;

    public ListApplicationsHandler(AppDbContext db) => _db = db;

    public async Task<SliceResult<ApplicationPage>> HandleAsync(
        ApplicationQuery query, CancellationToken ct = default)
    {
        // Defaults resolved once, here, for whichever surface called.
        var pageNumber = query.Page ?? 1;
        var pageSize = query.PageSize ?? DefaultPageSize;

        // Validation in the handler, so both surfaces get the same answer.
        // Note these REJECT rather than clamp: silently turning ?page=0 into
        // page 1 hands the caller a page they did not ask for and no way to
        // tell. An out-of-range page is a bug in the caller, and a 400 says so.
        if (pageNumber < 1)
            return SliceResult<ApplicationPage>.Invalid("page must be 1 or greater.");
        if (pageSize < 1 || pageSize > MaxPageSize)
            return SliceResult<ApplicationPage>.Invalid($"pageSize must be between 1 and {MaxPageSize}.");
        if (query.AppliedFrom is not null && query.AppliedTo is not null &&
            query.AppliedFrom > query.AppliedTo)
            return SliceResult<ApplicationPage>.Invalid("appliedFrom must not be after appliedTo.");

        // AsNoTracking: this is a read that never writes back, so there is no
        // reason to pay for the change tracker's snapshot of every row.
        var applications = _db.JobApplications.AsNoTracking();

        if (query.Status is not null)
            applications = applications.Where(a => a.Status == query.Status);

        // ILIKE, not ==: "canva" should find "Canva". EF.Functions.ILike maps to
        // Postgres's ILIKE operator, so the match happens in SQL rather than by
        // pulling rows into memory to compare them — the same discipline as
        // "aggregate in SQL, not in C#".
        //
        // The patterns are built into locals first. Inside the lambda they are
        // captured constants EF parameterises, which also means the caller's
        // text never reaches SQL as text.
        var company = Pattern(query.Company);
        if (company is not null)
            applications = applications.Where(a =>
                EF.Functions.ILike(a.Posting.Company.Name, company, EscapeChar));

        var title = Pattern(query.Title);
        if (title is not null)
            applications = applications.Where(a =>
                EF.Functions.ILike(a.Posting.Title, title, EscapeChar));

        // The JOIN that justifies the whole storage decision. Any() over the
        // join collection becomes an EXISTS against posting_skills -> skills;
        // in a denormalized store the same question means reading every
        // document. Matched case-insensitively but *exactly* — no surrounding
        // wildcards — because asking for "C" should not return every C# and
        // C++ posting.
        var skill = Escape(query.Skill);
        if (skill is not null)
            applications = applications.Where(a => a.Posting.PostingSkills
                .Any(ps => EF.Functions.ILike(ps.Skill.Name, skill, EscapeChar)));

        if (query.AppliedFrom is not null)
            applications = applications.Where(a => a.DateApplied >= query.AppliedFrom);
        if (query.AppliedTo is not null)
            applications = applications.Where(a => a.DateApplied <= query.AppliedTo);

        // Counted before paging, so totalCount describes the filter's whole
        // result set rather than the slice of it on this page — that is what
        // lets a caller compute how many pages there are.
        var totalCount = await applications.CountAsync(ct);

        var page = await Sort(applications, query)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new ApplicationListItem(
                a.Id,
                a.Posting.Company.Name,
                a.Posting.Title,
                a.Posting.Location,
                a.Status,
                a.DateApplied,
                a.Posting.PostingSkills.Select(ps => ps.Skill.Name).ToList()))
            .ToListAsync(ct);

        return SliceResult<ApplicationPage>.Ok(new ApplicationPage(
            page,
            totalCount,
            pageNumber,
            pageSize,
            (int)Math.Ceiling(totalCount / (double)pageSize)));
    }

    // An exact ILIKE term with the wildcards taken out, or null when there is
    // nothing to filter on.
    //
    // % and _ are ILIKE wildcards, so a caller searching for "Ampol_Energy" would
    // otherwise get a single-character wildcard rather than an underscore. They
    // are escaped with a backslash, which ILike's third argument declares as the
    // escape character. Not a security hole — the value is still parameterised,
    // never concatenated into SQL — but it is a correctness one.
    private static string? Escape(string? term) =>
        string.IsNullOrWhiteSpace(term)
            ? null
            : term.Trim()
                .Replace(EscapeChar, EscapeChar + EscapeChar)
                .Replace("%", EscapeChar + "%")
                .Replace("_", EscapeChar + "_");

    // The same, wrapped in % … % so it matches anywhere in the column.
    private static string? Pattern(string? term) =>
        Escape(term) is { } escaped ? $"%{escaped}%" : null;

    // Sorting by an enum the caller picked, translated to a real ORDER BY.
    //
    // The ThenBy(a => a.Id) is load-bearing, not tidiness. DateApplied is a
    // DateOnly, so every application logged on the same day ties — and OFFSET
    // with a non-deterministic ORDER BY lets Postgres return a row on page 1
    // and again on page 2 while another row is never shown at all. Phase 2.2
    // hit the same tie from the other side and had to drop an ordering
    // assertion as flaky (phase-2.2-tests-and-ci.md); a stable tiebreak is what
    // makes both the paging correct and that assertion writable again.
    private static IQueryable<JobApplication> Sort(
        IQueryable<JobApplication> applications, ApplicationQuery query)
    {
        // Newest first by default: the list you open is "what have I applied to
        // lately", not "what did I apply to first".
        var ascending = (query.Direction ?? SortDirection.Desc) == SortDirection.Asc;

        var sorted = query.Sort switch
        {
            ApplicationSort.Company => ascending
                ? applications.OrderBy(a => a.Posting.Company.Name)
                : applications.OrderByDescending(a => a.Posting.Company.Name),
            ApplicationSort.Title => ascending
                ? applications.OrderBy(a => a.Posting.Title)
                : applications.OrderByDescending(a => a.Posting.Title),
            // Status is stored as a string (HasConversion<string>), so this is
            // an alphabetical sort, not a lifecycle one. It happens to agree
            // with the enum's declaration order — Applied, Interviewing, Offer,
            // Rejected, Withdrawn — which is luck, not design. Phase 2.5 defines
            // the real lifecycle; if a stage is ever renamed or inserted, this
            // sort silently stops meaning what its name suggests.
            ApplicationSort.Status => ascending
                ? applications.OrderBy(a => a.Status)
                : applications.OrderByDescending(a => a.Status),
            ApplicationSort.UpdatedAt => ascending
                ? applications.OrderBy(a => a.UpdatedAtUtc)
                : applications.OrderByDescending(a => a.UpdatedAtUtc),
            _ => ascending
                ? applications.OrderBy(a => a.DateApplied)
                : applications.OrderByDescending(a => a.DateApplied),
        };

        return sorted.ThenBy(a => a.Id);
    }
}
