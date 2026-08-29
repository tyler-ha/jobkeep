# Phase 7 — feature expansion, after the front end exists

**Status: placeholder.** Nothing here is scheduled and nothing here is committed.

## What this doc is, and what it is not

**It is not a feature list.** [`backlog.md`](../backlog.md) already holds those,
with sizes, likely homes and the reasoning behind each deferral. Copying them here
would create a second register, and two registers drift.

**It is the record of what changes about *building* a feature** once Phase 6 has
shipped a front end — written now, at the checkpoint, rather than discovered later
when an estimate turns out to be half the real number.

The commit where that changes is tagged:

```
checkpoint/backend-complete        # git show checkpoint/backend-complete
```

Everything before it was a backend-only project.

## The one rule: a feature now has two halves

Through Phase 5, adding a feature meant:

> a slice file under `src/Modules/<Module>/`, two lines in `<Module>Module.cs`,
> a field or two on `Query.cs` / `Mutation.cs`, and an integration test.

That is still true, and it is now **the first half of the job**. The second half
is a screen or a component, the fetch call behind it, and a visual that stays
inside the approved token system without inventing a colour.

Two consequences worth stating plainly, because both are easy to get wrong:

- **Estimates carried over from Phase 2–5 will be roughly half of the real cost.**
  "It's just one slice" was an honest description of a whole feature then. It
  describes half of one now.
- **A backend-only change can still be incomplete.** Shipping
  `POST /resumes/{id}/skills` in Phase 5 without its `DELETE` inverse was
  defensible while there was no UI — nothing could drag the wrong skill on. The
  moment a screen existed, the missing half became a hole in a shipped
  interaction, and step 6.1 had to go back for it. Ask what the UI would need,
  not just what the API can express.

## The checklist for one feature

1. **Backend slice**, in the owning module. `AppDbContext` directly, validate in
   the handler, return a DTO. The rules in `CLAUDE.md` under "Where new code goes"
   are unchanged.
2. **Both surfaces.** REST route *and* GraphQL field, over the same handler, so a
   rule cannot mean two things. File upload is the one standing exception
   (`DocumentsModule.cs` argues it).
3. **Integration test through the real surface**, not a unit test with a fake —
   plus a parity test if the feature adds a new failure mode.
4. **Front-end surface.** Whatever screen it lands on, or a new one.
5. **Tokens only.** No new colours, no new type family. The amber budget is one
   held moment per screen plus the two functional uses; a missing or absent thing
   never takes the alert red. If a feature seems to need a new token, that is a
   design decision to raise, not a CSS variable to add.
6. **Update the route table** in
   [`phase-6-frontend.md`](phase-6-frontend.md) — it is the snapshot the front end
   was built against, and a feature that adds an endpoint makes it stale.
7. **Ask before adding a dependency.** Standing instruction from the user, on both
   sides of the stack.

## Where front-end code goes

**To be filled in by step 6.2**, when the app is actually scaffolded. It should
end up as short and as prescriptive as the "Where new code goes" block in
`CLAUDE.md`: one paragraph naming the directory a screen goes in, the directory a
shared component goes in, and where the API calls live.

Left deliberately empty rather than guessed. A structure invented before the
scaffold exists would be wrong in the specifics and would still get followed.

## Which backlog items visibly reshape the front end

Pointers only — the rows themselves, with sizes and reasoning, live in
[`backlog.md`](../backlog.md). Listed by how much *front-end* work they imply,
which is not the same ordering the backlog uses:

| Backlog item | What it does to the UI |
|---|---|
| **Reminders / follow-ups** | Gives the Today screen its reason to exist. Today is currently a summary; with due dates it becomes the screen you actually open first. |
| **Interview rounds** | Reshapes the Pipeline board. `Interviewing` is one column today; rounds make it a column with depth, and the drag semantics have to answer what moving a card between rounds means. |
| **Contacts / recruiter tracking** | A ninth screen, plus a presence on the Job post detail. The first genuinely new surface. |
| **Target profile** | Reshapes Insights. Phase 2.4 answers "what is in demand"; a target changes it to "what is in demand that I don't have yet" — a different chart, not an extra row. |
| **Soft delete / archive** | Touches **every list**: an archive filter, an empty state that distinguishes "none" from "none active", and an undo affordance. Cheap on the backend, wide on the front. |
| **Authentication / multi-user** | Touches all of it, plus a login, plus every fetch growing a credential. Also forces the CORS policy added in 6.1 to be revisited — `AllowAnyOrigin` and credentials are mutually exclusive, and `Program.cs` says so at the point it matters. |
| **Data export (CSV/JSON)** | Nearly free on both sides — a read endpoint and a button. The cheapest real feature on the list once a UI exists. |

## What does not change

- **The architecture rules.** Modules, slices, one rule / one implementation,
  aggregate in SQL, DTOs at the edge. A front end consumes the API; it does not
  get to reach past it, and "the UI needs it shaped differently" is an argument
  for a new slice, not for returning an entity.
- **Cost stays near-zero.** A static front end is free to host; nothing here
  introduces something that bills per hour.
- **Phases end runnable.** A feature whose backend shipped and whose screen did
  not is a half-applied change, which is exactly what priority 2 exists to stop.
