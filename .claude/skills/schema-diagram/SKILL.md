---
name: schema-diagram
description: Redraw the JobKeep database ERD and architecture diagram as committed SVG files in docs/diagrams/. Use when the schema changes (a new entity, a new migration, a changed delete rule or index), when a module boundary moves, or when the user asks to visualise/diagram/redraw the database, schema, ERD, or architecture. Derives the schema from EF Core rather than by reading model classes.
allowed-tools: Bash, Read, Write, Edit, Grep, Glob
---

# Schema & architecture diagrams

Redraws `docs/diagrams/schema-erd.svg` and `docs/diagrams/architecture.svg`.

These are **committed artefacts referenced from README.md and
`docs/architecture.md`.** They go stale silently — nothing fails a build when
the schema moves and the picture doesn't. Redraw them in the same change that
moves the schema, not later.

## The rule that makes this skill worth having

**Derive the schema from EF, never from reading the model classes.**

`Models/*.cs` tells you what the C# looks like. It does *not* reliably tell you
the column type, the precision, the delete behaviour, or which indexes are
unique — those come from Fluent API config in `src/Data/AppDbContext.cs`, from
EF's own conventions, and from the Npgsql provider's type mapping. Reading the
models and inferring the DDL produces a diagram that is subtly wrong in exactly
the places an interviewer would probe.

## Step 1 — pull the real DDL

From `src/`:

```bash
dotnet tool restore
dotnet ef migrations script --idempotent -o <scratch>/schema.sql
```

`-o` is **not optional**. Without it EF writes to the current directory and
fails with `UnauthorizedAccessException: Access to the path '...\src' is denied`.
Write it to a scratch path, never into the repo.

## Step 2 — confirm the model and the migrations agree

```bash
dotnet ef migrations has-pending-model-changes
```

Expected: `No changes have been made to the model since the last migration.`

If it reports pending changes, **stop and say so.** The migration is behind
`AppDbContext.cs`, so the DDL you just generated is not the current model, and a
diagram drawn from it would be a picture of the past. Either the user adds the
migration first, or the diagram gets an explicit "as of migration X" caveat.

## Step 3 — read the DDL, not the models

Take from `schema.sql`: table names, column names and types exactly as written
(`numeric(12,2)`, `timestamptz`, `text[]`, `varchar(200)`), `NOT NULL`,
`PRIMARY KEY`, `FOREIGN KEY ... ON DELETE`, and every `CREATE [UNIQUE] INDEX`.

Go to the source only for what DDL cannot carry:
- enum member names → `src/Models/Enums.cs` (stored as strings, so the names
  are the values in the database)
- the *reasoning* behind a delete rule or a denormalisation → the comments in
  `src/Data/AppDbContext.cs`, which are deliberately dense and are the
  interview material

## Step 4 — draw

Hand-author SVG. No libraries, no runtime, no external assets — these files are
committed and must render in GitHub's markdown viewer, which strips scripts and
does not reliably honour `prefers-color-scheme` inside an SVG.

**Therefore: paint an explicit background rect and use literal colours** that
read on both GitHub light and dark. Do not rely on `currentColor` or media
queries here — that is the right technique for an Artifact HTML page and the
wrong one for a committed `.svg`.

Palette in use (keep it stable across redraws):

| role | hex | meaning |
|---|---|---|
| ground | `#FBFCFB` | page/background rect |
| surface | `#FFFFFF` | table box fill |
| header | `#E8EEEB` | table header strip |
| ink | `#16211F` | table + column names |
| muted | `#5B6C68` | types, cardinality labels |
| line | `#C9D2CE` | borders, rules |
| accent | `#0E5F58` | `ON DELETE RESTRICT` edges, PK tags |
| cascade | `#A8402F` | `ON DELETE CASCADE` edges |

Delete behaviour is the one thing in this schema that is genuinely a
*decision*, so it gets the strongest encoding: **solid accent = RESTRICT,
dashed cascade-colour = CASCADE**, explained in a legend. Colour alone is not
enough — keep the dash pattern so the distinction survives greyscale and
colour-blindness.

Other drawing rules:
- Arrows point **from parent to the table holding the foreign key.**
- Label every edge with cardinality (`1 : N`, `1 : 1`). A 1:1 that is enforced
  by a unique index on the FK should say so, since that is a constraint rather
  than a convention.
- Show only keys in the ERD boxes (PK / FK / UNIQUE). The full column list
  belongs in prose or a table beneath, not crammed into the drawing — a
  diagram is for *relationships*.
- Align to a grid and keep box heights uniform. Even gaps are most of what
  makes a hand-drawn diagram read as deliberate.
- Set `viewBox` to the content and let the README scale it. Include
  `role="img"` and a `<title>`/`<desc>` so the diagram is not opaque to a
  screen reader.

## Step 5 — verify before claiming it is done

- [ ] `has-pending-model-changes` reported no changes (or the caveat is written on the diagram)
- [ ] every table in `schema.sql` appears in the ERD — count them
- [ ] every `FOREIGN KEY` in `schema.sql` is an edge, with the right `ON DELETE` style
- [ ] every `CREATE UNIQUE INDEX` is reflected (UNIQUE tag, or a `1 : 1` cardinality)
- [ ] no two elements overlap; open the file and look at it
- [ ] the footer counts (tables / indexes / FKs / cascade vs restrict) match `schema.sql`

Counting is not optional. A diagram that silently omits a table is worse than
no diagram, because it will be trusted.

## Architecture diagram

`docs/diagrams/architecture.svg` is drawn from `docs/architecture.md`, which is
the authority — not from the current folder layout.

The thing this diagram must show, and the reason it exists: **REST and GraphQL
are two surfaces over one data layer**, so a rule implemented in a slice is
enforced identically on both. Draw the shared path, not two parallel stacks.

Draw what is actually there, checking `CLAUDE.md` first. Until Phase 2.3 that
meant a second, dashed lane for `src/Endpoints/` + `src/Repositories/`, which
were still wired up and *retiring, not growing*. **Phase 2.3 deleted them**, so
there is one lane now and the dashed treatment should be gone. The general rule
survives the specific case: a diagram of the intended end state, presented as
the current state, is the failure mode to avoid — and so is a diagram still
showing scaffolding that has since been removed.

## Do not

- Do not use `mcpmarket-me:diagramming-code` for either diagram. It parses C#
  (`--language c_sharp`, not `csharp`) and is good at call graphs and
  complexity hotspots, but `--type module-deps` returns `No import edges found`
  for C# — it does not resolve `using` directives — and `class-hierarchy`
  emits a flat list of mangled names including EF migration Designer files.
  Its own docs exclude "architecture diagrams not derived from code".
- Do not write generated SQL into the repo. Scratch only.
- Do not edit README.md as a side effect. Changing what the README shows is
  the user's call; ask.
