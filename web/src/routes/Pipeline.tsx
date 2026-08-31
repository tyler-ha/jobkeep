import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  DndContext,
  DragOverlay,
  PointerSensor,
  useDraggable,
  useDroppable,
  useSensor,
  useSensors,
  type DragEndEvent,
  type DragStartEvent,
} from '@dnd-kit/core';
import { GripVertical } from 'lucide-react';

import { Failure } from '../components/Failure';
import { Screen } from '../components/Screen';
import {
  APPLICATION_STATUSES,
  ApiError,
  asApiError,
  listApplications,
  updateApplication,
  type ApplicationListItem,
  type ApplicationStatus,
} from '../lib/api';
import { formatDateOnly } from '../lib/format';

/* The board. Five columns, one per ApplicationStatus — there is no "Saved" and
 * no "Screening" in Models/Enums.cs, so there are no columns for them.
 *
 * The move IS the write. Dropping a card on a column is
 * `PATCH /applications/{id}` with a new status, and the important thing about
 * that is the failure mode: the lifecycle in Models/ApplicationStatusTransitions.cs
 * is deliberately permissive but not empty — an Offer can only be reached from
 * an active application — so a 400 here is a RULE, not a fault. It gets the
 * amber ground and the card goes back where it came from. Only a real failure
 * gets the alert red.
 *
 * As on the ATS board, dnd-kit's KeyboardSensor is deliberately not mounted and
 * the grip stays out of the accessibility tree. Every card carries a "Move to"
 * select instead, which is one control instead of press-arrow-arrow-press, does
 * not need a live region to be usable, and works on a phone.
 */

/* The API caps pageSize at 100 and rejects anything larger (ListApplications.cs
 * — a cap, not a clamp). A board is only honest if it holds everything, so the
 * remaining pages are fetched too, up to a ceiling: past this many the board is
 * the wrong tool anyway, and the footer says plainly what is not on it rather
 * than quietly showing a subset. */
const PAGE_SIZE = 100;
const MAX_PAGES = 5;

type Load =
  | { tag: 'loading' }
  | { tag: 'error'; error: ApiError }
  | { tag: 'ready'; items: ApplicationListItem[]; total: number };

export default function Pipeline() {
  const [load, setLoad] = useState<Load>({ tag: 'loading' });
  const [dragging, setDragging] = useState<ApplicationListItem | null>(null);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [moveError, setMoveError] = useState<ApiError | null>(null);
  const [announce, setAnnounce] = useState('');

  /* Pointer only — see the header note. Four pixels of travel before a drag
   * starts, so a click on the card's link is still a click. */
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 4 } }));

  useEffect(() => {
    let live = true;

    (async () => {
      try {
        const q = (p: number) =>
          `?page=${p}&pageSize=${PAGE_SIZE}&sort=DateApplied&direction=Desc`;
        const first = await listApplications(q(1));
        const items = [...first.items];

        /* Almost never runs: a job search with more than a hundred live
         * applications is not the common case. It is here so that when it does
         * happen the board is not silently wrong. */
        for (let p = 2; p <= Math.min(first.totalPages, MAX_PAGES); p++) {
          const next = await listApplications(q(p));
          items.push(...next.items);
        }

        if (live) setLoad({ tag: 'ready', items, total: first.totalCount });
      } catch (e) {
        if (live) setLoad({ tag: 'error', error: asApiError(e) });
      }
    })();

    return () => {
      live = false;
    };
  }, []);

  const columns = useMemo(() => {
    const by = new Map<ApplicationStatus, ApplicationListItem[]>(
      APPLICATION_STATUSES.map((s) => [s, []]),
    );
    if (load.tag === 'ready') for (const a of load.items) by.get(a.status)?.push(a);
    return by;
  }, [load]);

  const move = useCallback(
    async (card: ApplicationListItem, to: ApplicationStatus) => {
      if (card.status === to) return;
      const from = card.status;

      setRefusal(null);
      setMoveError(null);

      /* Optimistic. The board is a direct-manipulation surface and a card that
       * waits for a round trip before it lands does not feel manipulated — so
       * it moves now and comes back if the API says no. Every path below either
       * confirms or reverts; none leaves the board disagreeing with the server. */
      setLoad((prev) =>
        prev.tag === 'ready'
          ? { ...prev, items: prev.items.map((a) => (a.id === card.id ? { ...a, status: to } : a)) }
          : prev,
      );
      setAnnounce(`${card.title} moved to ${to}.`);

      try {
        await updateApplication(card.id, { status: to });
      } catch (e) {
        const err = asApiError(e);
        setLoad((prev) =>
          prev.tag === 'ready'
            ? {
                ...prev,
                items: prev.items.map((a) => (a.id === card.id ? { ...a, status: from } : a)),
              }
            : prev,
        );
        /* The whole reason this screen needs a distinct state. The lifecycle
         * refusing a move is the domain working, and the copy says which move
         * was refused rather than "something went wrong". */
        if (err.isRuleRefusal) {
          setRefusal(`${card.title} cannot go from ${from} to ${to}. ${err.message}`);
          setAnnounce(`${card.title} could not move to ${to}. ${err.message}`);
        } else {
          setMoveError(err);
          setAnnounce(`${card.title} could not move to ${to}.`);
        }
      }
    },
    [],
  );

  function onDragStart(e: DragStartEvent) {
    if (load.tag !== 'ready') return;
    setDragging(load.items.find((a) => a.id === e.active.id) ?? null);
  }

  function onDragEnd(e: DragEndEvent) {
    const card = dragging;
    setDragging(null);
    const to = e.over?.id as ApplicationStatus | undefined;
    if (card && to) void move(card, to);
  }

  const truncated =
    load.tag === 'ready' && load.total > load.items.length ? load.total - load.items.length : 0;

  return (
    <Screen
      title="Pipeline"
      lede="Every application as a card. Drag one across to move it, or use the control on the card."
    >
      {load.tag === 'error' && <Failure error={load.error} what="load your board" />}
      {load.tag === 'loading' && (
        <p className="quiet" aria-live="polite">
          Loading…
        </p>
      )}

      {/* A refused move and a failed one read completely differently and are
          rendered differently: amber for the rule, red for the fault. */}
      {refusal && (
        <p className="refusal" role="status">
          <strong>Not a move this application can make.</strong> {refusal}
        </p>
      )}
      {moveError && <Failure error={moveError} what="move that application" />}

      {/* Both paths — the drag and the select — announce through this one
          region, so neither needs its own. */}
      <p className="sr-only" role="status" aria-live="polite">
        {announce}
      </p>

      {load.tag === 'ready' && load.items.length === 0 && (
        <div className="state">
          <h2>Nothing on the board yet</h2>
          <p>
            The board shows the applications you have recorded.{' '}
            <Link to="/applications">Add one</Link> or <Link to="/import">import an ad</Link>, and
            it appears in Applied.
          </p>
        </div>
      )}

      {load.tag === 'ready' && load.items.length > 0 && (
        <DndContext sensors={sensors} onDragStart={onDragStart} onDragEnd={onDragEnd}>
          <div className="pipeline">
            {APPLICATION_STATUSES.map((status) => (
              <Column
                key={status}
                status={status}
                cards={columns.get(status) ?? []}
                dragging={dragging}
                onMove={move}
              />
            ))}
          </div>

          {/* The card under the cursor, drawn once at the top layer rather than
              by moving the original — so a card being dragged out of a scrolled
              column is not clipped by it. */}
          <DragOverlay dropAnimation={null}>
            {dragging ? <CardFace a={dragging} floating /> : null}
          </DragOverlay>
        </DndContext>
      )}

      {truncated > 0 && (
        <p className="insight-foot quiet">
          The board holds the {load.tag === 'ready' ? load.items.length : 0} most recent.{' '}
          <span className="num">{truncated}</span> older{' '}
          {truncated === 1 ? 'application is' : 'applications are'} not shown —{' '}
          <Link to="/applications">the list</Link> has all of them.
        </p>
      )}
    </Screen>
  );
}

/* ---- A column ------------------------------------------------------------ */

function Column({
  status,
  cards,
  dragging,
  onMove,
}: {
  status: ApplicationStatus;
  cards: ApplicationListItem[];
  dragging: ApplicationListItem | null;
  onMove: (card: ApplicationListItem, to: ApplicationStatus) => void;
}) {
  const { setNodeRef, isOver } = useDroppable({ id: status });

  /* Armed = a drag is in flight and this column is somewhere it could go.
   * Over = the cursor is on it. Two states, because the board should show where
   * a card MAY land before the cursor gets there. The card's own column is not
   * armed: dropping it back is a no-op and lighting it up would promise a move
   * that will not happen. */
  const armed = dragging != null && dragging.status !== status;

  return (
    <section
      ref={setNodeRef}
      className="col"
      data-armed={armed || undefined}
      data-over={(armed && isOver) || undefined}
      aria-labelledby={`col-${status}`}
    >
      <header className="col-head">
        <h2 id={`col-${status}`}>
          <span className="col-dot" data-status={status} aria-hidden />
          {status}
        </h2>
        <span className="col-count num">{cards.length}</span>
      </header>

      {/* Colour cannot carry the hot state on its own — --pop is 1.45 on the
          ground, under the 3.0 non-text threshold — so the armed column changes
          its outline and shows this label as well as its ground. */}
      {armed && (
        <p className="col-hint" aria-hidden>
          {isOver ? `Drop to move here` : `Move here`}
        </p>
      )}

      <ul className="col-cards">
        {cards.map((a) => (
          <li key={a.id}>
            <Card a={a} onMove={onMove} />
          </li>
        ))}
      </ul>

      {cards.length === 0 && !armed && <p className="col-empty quiet">Nothing here.</p>}
    </section>
  );
}

/* ---- A card -------------------------------------------------------------- */

function Card({
  a,
  onMove,
}: {
  a: ApplicationListItem;
  onMove: (card: ApplicationListItem, to: ApplicationStatus) => void;
}) {
  const { listeners, setNodeRef, isDragging } = useDraggable({ id: a.id });

  return (
    <div ref={setNodeRef} className="card-wrap" data-dragging={isDragging || undefined}>
      {/* The grip carries the pointer listeners and nothing else. dnd-kit's
          `attributes` is deliberately not destructured at all: spreading it adds
          role="button", a tabindex and an aria-roledescription, which would put
          a focus stop on every card for an interaction the keyboard cannot
          start — the select below is the keyboard path, and it is one control
          instead of press-arrow-arrow-press. */}
      <span className="card-grip" {...listeners} aria-hidden>
        <GripVertical size={14} />
      </span>

      <CardFace a={a} />

      <label className="card-move">
        <span className="sr-only">Move {a.title} to</span>
        <select
          value=""
          onChange={(e) => {
            const to = e.target.value as ApplicationStatus;
            if (to) onMove(a, to);
          }}
        >
          <option value="">Move to…</option>
          {APPLICATION_STATUSES.filter((s) => s !== a.status).map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </select>
      </label>
    </div>
  );
}

/* The card's face, shared by the board and the drag overlay so the thing under
 * the cursor is the same object that was picked up rather than a lookalike. */
function CardFace({ a, floating }: { a: ApplicationListItem; floating?: boolean }) {
  return (
    <div className="card-face" data-floating={floating || undefined}>
      <p className="card-role">
        {floating ? a.title : <Link to={`/applications/${a.id}`}>{a.title}</Link>}
      </p>
      <p className="card-company">{a.company}</p>
      <p className="card-meta">
        <time dateTime={a.dateApplied}>{formatDateOnly(a.dateApplied)}</time>
        {a.skills.length > 0 && (
          <>
            {' · '}
            <span className="num">{a.skills.length}</span>{' '}
            {a.skills.length === 1 ? 'skill' : 'skills'}
          </>
        )}
      </p>
    </div>
  );
}
