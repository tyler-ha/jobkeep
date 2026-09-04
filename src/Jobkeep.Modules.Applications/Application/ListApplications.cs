using Jobkeep.Contracts.Shared;
using Jobkeep.Contracts.Skills;
using Jobkeep.Modules.Applications.Domain;
using Jobkeep.SharedKernel;
using Mediator;
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
    // PHASE 9, gap 2 — a SET, where this was one value.
    //
    // The single value is why the Applications screen has no "Closed" tab: the
    // union of two requests cannot be paged honestly, because page 2 of "Rejected"
    // and page 2 of "Withdrawn" are not page 2 of anything a user asked for.
    //
    // NEITHER SURFACE BREAKS, and both for a reason worth knowing rather than
    // testing by luck:
    //
    //   * REST binds a repeated query parameter to an array, so `?status=Applied`
    //     still arrives — as a one-element array. No existing URL changes meaning.
    //   * GraphQL COERCES a single value to a list of one (spec: input coercion for
    //     list types). So `applications(query: { status: APPLIED })` keeps working
    //     against `[ApplicationStatus!]`, unedited.
    //
    // Both are pinned by tests, because "the spec says so" is exactly the kind of
    // claim that is true until a serializer setting disagrees.
    public ApplicationStatus[]? Status { get; init; }

    // Sugar over the above, and the reason it is not just left to the caller is
    // that "closed" is a DOMAIN fact rather than a client preference.
    //
    // ApplicationStatusTransitions has treated Rejected and Withdrawn as closed
    // since Phase 2.5 and enforces a rule that depends on it — an Offer can only be
    // reached from an active application. If the front end spelled out
    // `?status=Rejected&status=Withdrawn` instead, that would be a SECOND copy of
    // the answer, in TypeScript, free to drift from the one the PATCH rule uses.
    // So the set is named once, in Domain/, and this reads it.
    //
    // Combining it with an explicit Status is REFUSED rather than merged or
    // silently ignored — see the handler. Two ways of saying which stages you want,
    // in one request, is a question with no answer the caller can predict.
    public bool? IsClosed { get; init; }
    public string? Company { get; init; }
    public string? Title { get; init; }
    public string? Skill { get; init; }
    public DateOnly? AppliedFrom { get; init; }
    public DateOnly? AppliedTo { get; init; }
    public ApplicationSort? Sort { get; init; }
    public SortDirection? Direction { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }

    // PHASE 8 — the archive filter. Nullable like the rest, and for the same
    // [AsParameters]/[FromQuery] reason: a non-nullable bool would bind as
    // required and `?includeArchived=` omitted would 400.
    //
    // The semantics are INCLUDE, not ONLY. `true` returns live and archived rows
    // together, which is what an "include archived" checkbox means to the person
    // ticking it. An archived-only view would be a third state, and the two
    // screens that would want it can filter the merged list they already have.
    public bool? IncludeArchived { get; init; }
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
    List<string> Skills,
    // PHASE 8. Always sent, and always false unless the caller asked for archived
    // rows — a row the filter would have hidden is the only one that can be true.
    // It is on the LIST item and not just the detail because the list is where a
    // mixed page has to be rendered, and a client that cannot tell which rows are
    // archived would have to infer it from the request it made, which is the kind
    // of state a UI gets wrong on the second render.
    bool IsArchived);

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

public record ListApplications(ApplicationQuery Query) : IRequest<SliceResult<ApplicationPage>>;

public class ListApplicationsHandler : IRequestHandler<ListApplications, SliceResult<ApplicationPage>>
{
    // A cap, not a preference. PageSize is caller-supplied and reaches Take()
    // directly; without a ceiling, `?pageSize=1000000` is a free denial of
    // service on an unauthenticated surface.
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 20;

    // Declared to Postgres as the ILIKE escape character; see Escape().
    private const string EscapeChar = @"\";

    private readonly ApplicationsDbContext _db;
    private readonly ISkillCatalog _skills;

    public ListApplicationsHandler(ApplicationsDbContext db, ISkillCatalog skills)
    {
        _db = db;
        _skills = skills;
    }

    // The page shape SQL can produce. Its skills are ids, because `skills` is
    // another module's table since 13.2d — see ApplicationDetail.cs, which makes
    // the same split for the same reason.
    private record ListRow(
        Guid Id,
        string Company,
        string Title,
        string? Location,
        ApplicationStatus Status,
        DateOnly DateApplied,
        List<Guid> SkillIds,
        bool IsArchived);

    public async ValueTask<SliceResult<ApplicationPage>> Handle(
        ListApplications message, CancellationToken ct)
    {
        var query = message.Query;
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

        // PHASE 9. Refused rather than resolved, because both resolutions are worse:
        // intersecting them answers a question nobody asked, and letting one win
        // silently means a caller who sent both never learns which. The message
        // names the fix rather than the fault.
        if (query.Status is { Length: > 0 } && query.IsClosed is not null)
            return SliceResult<ApplicationPage>.Invalid(
                "Pass either status or isClosed, not both — isClosed is shorthand for "
                + "the closed stages, so naming statuses as well says the same thing twice.");
        if (query.AppliedFrom is not null && query.AppliedTo is not null &&
            query.AppliedFrom > query.AppliedTo)
            return SliceResult<ApplicationPage>.Invalid("appliedFrom must not be after appliedTo.");

        // AsNoTracking: this is a read that never writes back, so there is no
        // reason to pay for the change tracker's snapshot of every row.
        var applications = _db.JobApplications.AsNoTracking();

        // PHASE 8. The default — archived rows excluded — costs nothing and is
        // written nowhere: it is the global query filter, and every other read in
        // this module gets it for free. Only the exception needs a line.
        //
        // IgnoreQueryFilters drops EVERY filter on the query, including the one on
        // job_postings that the joins below reach through. That is correct rather
        // than incidental: an application archived after its ad was archived would
        // otherwise be dropped by the inner join to a posting the filter hides, so
        // a caller asking to see archived rows would be handed a page missing some
        // of them, with no indication that anything was withheld.
        if (query.IncludeArchived == true)
            applications = applications.IgnoreQueryFilters();

        // Contains over an array translates to SQL `IN`, so this stays one round
        // trip whether the caller named one stage or five.
        //
        // An EMPTY array is treated as "no filter" rather than "match nothing", and
        // the two surfaces differ on whether that is even reachable — measured, not
        // assumed, because the first version of this comment guessed wrong:
        //
        //   * GraphQL CAN send it. `status: []` is a well-formed list of zero enum
        //     values and binds to an empty array, so this branch is live.
        //   * REST CANNOT. `?status=` binds an empty string to an ApplicationStatus,
        //     fails model binding, and answers 400 — the same as `?status=Banana`,
        //     which is the right answer and is already what the surface does.
        //
        // So the guard exists for GraphQL, and "no filter" is the reading that keeps
        // `{ status: [] }` meaning the same as omitting it.
        if (query.Status is { Length: > 0 } statuses)
            applications = applications.Where(a => statuses.Contains(a.Status));
        else if (query.IsClosed is { } closed)
        {
            // The set comes from Domain/, so REST, GraphQL and the PATCH rule cannot
            // disagree about which stages are closed.
            var closedStages = ApplicationStatusTransitions.Closed.ToArray();
            applications = closed
                ? applications.Where(a => closedStages.Contains(a.Status))
                : applications.Where(a => !closedStages.Contains(a.Status));
        }

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
        // join collection becomes an EXISTS against posting_skills; in a
        // denormalized store the same question means reading every document.
        //
        // ------------------------------------------------------------------
        // PHASE 13.2d — a name lookup, then an EXISTS on the id
        // ------------------------------------------------------------------
        // This used to be `ILike(ps.Skill.Name, skill, EscapeChar)` inside the
        // EXISTS, which reached through posting_skills into `skills` — a join
        // across a future service boundary that named no DbSet, so nothing
        // flagged it. Resolving the name first costs one extra query and turns
        // the EXISTS into a plain id comparison, which is what still works when
        // the taxonomy lives somewhere else.
        //
        // The semantics do not move. The old filter was already matched
        // case-insensitively but *exactly* — no surrounding wildcards, because
        // asking for "C" should not return every C# and C++ posting — and the
        // catalog's natural key is the same comparison, so the escaping this
        // needed for ILIKE simply stops being necessary.
        //
        // A skill nobody has ever recorded yields no rows rather than every row,
        // which is the answer a user filtering by it expects. Written as an
        // impossible predicate rather than an early return so that TotalCount and
        // the empty page below are produced by the same code path as every other
        // filter.
        if (!string.IsNullOrWhiteSpace(query.Skill))
        {
            var match = await _skills.FindByNameAsync(query.Skill, ct);
            applications = match is null
                ? applications.Where(a => false)
                : applications.Where(a => a.Posting.PostingSkills.Any(ps => ps.SkillId == match.Id));
        }

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
            .Select(a => new ListRow(
                a.Id,
                a.Posting.Company.Name,
                a.Posting.Title,
                a.Posting.Location,
                a.Status,
                a.DateApplied,
                a.Posting.PostingSkills.Select(ps => ps.SkillId).ToList(),
                a.IsDeleted))
            .ToListAsync(ct);

        // ONE call for the whole page's skills, across every row — which is why
        // ISkillCatalog.GetAsync is batched and de-duplicates its input. Twenty
        // applications naming the same handful of skills is one query with a
        // short parameter list, not twenty.
        var names = await _skills.GetAsync(
            page.SelectMany(r => r.SkillIds).ToList(), ct);

        var items = page
            .Select(r => new ApplicationListItem(
                r.Id,
                r.Company,
                r.Title,
                r.Location,
                r.Status,
                r.DateApplied,
                // Ordered, which the SQL version was not — Postgres returned the
                // join rows in whatever order suited it, so a card's chips could
                // reshuffle between requests. Sorting here costs nothing on a
                // handful of already-materialised strings and makes the list
                // stable, which a list of cards should be.
                r.SkillIds
                    .Where(names.ContainsKey)
                    .Select(id => names[id].Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                r.IsArchived))
            .ToList();

        return SliceResult<ApplicationPage>.Ok(new ApplicationPage(
            items,
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
