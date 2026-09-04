using Jobkeep.Contracts.Shared;
using Jobkeep.Modules.Applications.Domain;
using Jobkeep.SharedKernel;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Jobkeep.Modules.Applications;

// Slice: the board. PHASE 9, gap 3.
//
// ListApplications caps pageSize at 100 and REJECTS above it — a cap, not a
// clamp, and right for a list. It is wrong for a board: the Pipeline holds every
// card at once, so it was fetching pages in a loop up to a ceiling of five and
// printing an honest footer for whatever it could not reach. Five requests to
// draw one screen, and a ceiling expressed in pages rather than in cards.
//
// This is deliberately NOT a general-purpose cursor API. Phase 12's note says
// whatever fixes this should wait until something other than one screen wants
// it. One screen still wants it, so this is the smallest read that serves that
// screen and nothing else — no filters, no sort, no paging, because the board
// has no controls for any of them.

// What a card actually renders, and no more. A list row carries skill NAMES; the
// board's card shows "· 3 skills" and never the names. So this carries the count
// and skips the ISkillCatalog round trip ListApplications has to make for every
// page. That is the whole reason this is a separate projection rather than a
// reuse of ApplicationListItem: the same rows, one query instead of two.
public record BoardCard(
    Guid Id,
    string Company,
    string Title,
    ApplicationStatus Status,
    DateOnly DateApplied,
    int SkillCount);

// Flat, not grouped into columns, and that is a decision rather than laziness.
// The board already groups client-side, and it MOVES cards between columns
// optimistically — with columns on the wire every drag would have to splice two
// arrays instead of changing one field, to save a grouping the client does in
// one pass over rows it already has.
//
// TotalCount is the count before the cap, so the screen can say how many are not
// on the board. Without it a full board and a truncated one look identical.
public record ApplicationBoard(List<BoardCard> Cards, int TotalCount);

public record GetBoard() : IRequest<SliceResult<ApplicationBoard>>;

public class GetBoardHandler : IRequestHandler<GetBoard, SliceResult<ApplicationBoard>>
{
    // The board's ceiling, in cards. The same number the front end reached by
    // looping five pages of a hundred — what changed is the number of requests,
    // not how much a full board costs to draw. Past this a board is the wrong
    // tool and the list is the right one, which is what the footer says.
    private const int MaxCards = 500;

    private readonly ApplicationsDbContext _db;

    public GetBoardHandler(ApplicationsDbContext db) => _db = db;

    public async ValueTask<SliceResult<ApplicationBoard>> Handle(
        GetBoard message, CancellationToken ct)
    {
        // No includeArchived, on purpose: PHASE 8's global query filter excludes
        // archived rows and the board wants exactly that. An archive is the thing
        // you take OFF the board.
        //
        // The projection is built once and both the count and the page are taken
        // FROM IT, which is not tidiness. job_postings carries a query filter too,
        // so reaching a.Posting makes this an inner join that would drop an
        // application whose ad was archived — and a count taken before that join
        // would report rows the board cannot show, i.e. a footer announcing
        // missing cards on a board that is complete.
        var applications = _db.JobApplications.AsNoTracking();

        // Counted THROUGH the projection rather than off the bare table, which is
        // not tidiness: job_postings carries a query filter too, so reaching
        // a.Posting makes this an inner join that would drop an application whose
        // ad was archived. A count taken before that join reports rows the board
        // cannot show — a footer announcing missing cards on a complete board.
        var total = await Project(applications).CountAsync(ct);

        // Newest first, then Id — the stable tiebreak ListApplications argues for at
        // length, needed here for the same reason from the other end: DateApplied is
        // a DateOnly, so a day's applications tie, and WHICH of them the cap keeps
        // must not vary between requests.
        //
        // The ORDER BY sits on the ENTITY query, before the projection, because EF
        // cannot translate an ordering over a constructor-projected record: its
        // members are not mapped back to columns. Measured — it throws rather than
        // degrading to client evaluation, which is the good version of that failure.
        var cards = await Project(applications
                .OrderByDescending(a => a.DateApplied)
                .ThenBy(a => a.Id)
                .Take(MaxCards))
            .ToListAsync(ct);

        return SliceResult<ApplicationBoard>.Ok(new ApplicationBoard(cards, total));
    }

    // Written once so the count and the page cannot disagree about which rows a
    // card is made of — see the count above.
    private static IQueryable<BoardCard> Project(IQueryable<JobApplication> applications) =>
        applications.Select(a => new BoardCard(
            a.Id,
            a.Posting.Company.Name,
            a.Posting.Title,
            a.Status,
            a.DateApplied,
            a.Posting.PostingSkills.Count));
}
