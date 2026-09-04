# Security & data-structure audit

**Status:** Audit complete 2026-08-25. Remediation **not started** — see §5.
**Scope:** The 8-table PostgreSQL schema as of migration `20260819115119_InitialCreate`,
plus the config and API surfaces that expose it.

> **2026-09-04 — read every "Phase 10" below as "the deploy that replaces it".**
> The AWS deploy was **dropped** (`architecture.md` decision 22); a free host is
> still to be chosen. The *triggers* are unchanged — these findings still come due
> when the API first becomes reachable — but any **AWS-specific mitigation named
> below is moot** (RDS `StorageEncrypted`, SSM Parameter Store, the Lambda
> specifics); its replacement is chosen with the host. **Phase 11 (auth) also moved
> to last** on the roadmap, keeping its number.

**Method:** Schema read from `AppDbContextModelSnapshot.cs` (the DDL ground truth),
not from `Models/*.cs`. GraphQL exposure verified against the emitted SDL from a
running instance, not inferred from attributes.

This document exists because of the standard set in `CLAUDE.md`: *"Write down the
tradeoff."* Authentication is deferred **legibly** — it is recorded in `backlog.md`,
in the gap register, and tied to the deploy (**Phase 10**) and auth (**Phase 11**). Encryption, PII handling, retention,
secrets management and backup are not deferred; **they had zero mentions in any
document before this one.** That is the difference between an accepted tradeoff and
an undocumented risk, and it is the gap this audit closes.

---

## 1. Summary

JobKeep stores a full résumé, private notes, salary expectations and a complete
job-search history. Today it does so with:

| Control | State |
|---|---|
| Row ownership / tenancy | **Absent** — no `UserId` on any of the 8 tables |
| Authentication | **Absent** — no auth middleware, every endpoint anonymous |
| PII classification / encryption | **Absent** — `ResumeText` is unbounded plaintext `text` |
| TLS on the DB connection | **Not required** — no `SSL Mode`; Npgsql defaults to opportunistic |
| Concurrency token | **Absent** — last-write-wins |
| Soft delete / archive | **Absent** — hard `DELETE` with cascades |
| Audit columns | **Partial, and already incorrect** (see F8) |
| Audit trail (*who changed what*) | **Absent** (backlogged) |
| DB-side defaults / CHECK constraints | **Absent entirely** |
| Retention / purge | **Absent**, and APP 11.2 applies |

None of this is alarming for a single-user tool on `localhost`. **All of it becomes
load-bearing at the deploy (**Phase 10**)**, which puts this schema on a public endpoint in front of
an RDS instance. The audit is therefore ordered by *what must be true before deploy*
rather than by theoretical severity.

---

## 2. The reference attribute set

What an industry-standard secure schema carries, used here as the yardstick.

### Tier 1 — the per-row control set

On every mutable table:

| Attribute | Purpose | JobKeep |
|---|---|---|
| Surrogate PK | Stable identity independent of natural keys | ✅ `uuid` on all 8 |
| `CreatedAtUtc` / `UpdatedAtUtc` | *When* | ⚠️ 2 of 8 tables, inconsistently |
| `CreatedBy` / `UpdatedBy` | *Who* | ❌ |
| Concurrency token | Lost-update prevention | ❌ |
| `IsDeleted` / `DeletedAtUtc` | Recoverable deletion | ❌ |
| **Owner / tenant column** | **The security-critical one** | ❌ |

Timestamps should be `timestamptz NOT NULL` with a **DB-side** `now()` default, not a
C# property initializer — a default that exists only in the application is not a
constraint, it is a convention.

On PostgreSQL the concurrency token costs **zero added columns**: Npgsql's
`UseXminAsConcurrencyToken()` maps the `xmin` system column every row already has.

### Tier 2 — data protection

- **In transit:** TLS with certificate verification (`sslmode=verify-full`). Anything
  weaker on a cloud DB is a plaintext connection you cannot detect.
- **At rest:** OWASP treats disk/volume encryption and column-level encryption as
  *alternatives* for meeting the requirement, not as a stack. For RDS, volume
  encryption via KMS satisfies it — with the constraint that **encryption can only be
  enabled when the instance is created.**
- **Least privilege:** a dedicated application role that is not the DB owner and not
  the RDS master user; no admin rights; table/column/row-level grants.
- **Row-level security** as defence-in-depth *behind* the application filter — it makes
  isolation a constraint the database enforces rather than a convention every future
  query must remember.
- **Backups** encrypted at rest and in transit.
- **Retention.** Australian Privacy Act **APP 11.2** requires personal information to
  be destroyed or de-identified once it is no longer needed for its purpose. A résumé
  duplicated per application and kept forever is squarely in scope.

### Tier 3 — integrity

CHECK constraints; DB-side defaults; bounded text lengths (an unbounded `text` column
on a public write endpoint is an unbounded-storage vector); and indexes that actually
cover the queries the app runs.

### Tier 4 — the audit trail proper

A separate append-only `audit_events` table: actor, entity, action, before/after,
timestamp. This is distinct from Tier 1 — `UpdatedAtUtc` answers *when* and can never
answer *what changed*.

### Domain attributes

The other half of "what attributes are needed". Against **schema.org `JobPosting`**,
the de-facto interchange standard (it is what Google Jobs consumes), `job_postings`
covers `title`, `datePosted`, `employmentType`, `baseSalary`, `hiringOrganization`
and `jobLocation` — but is missing:

| Missing | schema.org name | Why it matters here |
|---|---|---|
| Expiry date | `validThrough` | A tracker that cannot tell you a posting closed is missing the one time-sensitive fact in the domain |
| Remote / hybrid / onsite | `jobLocationType` | The most-filtered attribute in the current market; `Location` as free text cannot answer it |
| Employer's requisition id | `identifier` | The natural key for de-duplicating the same role seen on two boards |
| Where you found it | *(no schema.org equivalent)* | Seek vs LinkedIn vs referral — an analytics question Phase 2.4 will want and cannot currently ask |

Against tracker convention, also missing: status **history**, interview rounds,
contacts, a next-action date, document versions. `backlog.md` already knows about
most of these; `validThrough`, `jobLocationType`, `identifier` and source/channel are
**new findings** and are not in the backlog today.

---

## 3. Findings

### S1 — High: resolve before the deploy (**Phase 10**)

**F1 · No owner column on any table, and no authentication.**
No `UserId`/`OwnerId`/`TenantId` anywhere; no `HasQueryFilter` in
`Data/AppDbContext.cs`; no `AddAuthentication`/`UseAuthorization` in `Program.cs`.
Every row is visible to every caller, and every endpoint is anonymous.
`architecture.md` §3 already predicts the cost of retrofitting — *"touches every
module's queries when it lands"* — which is exactly right: it means backfilling all
8 tables and re-scoping every query written between now and then. **Every phase built
before this lands adds to the size of that change.**

**F2 · PII stored plaintext, unclassified, with no retention rule.**
`job_applications.ResumeText` is an unbounded `text` column holding a full résumé —
name, contact details, employment history. It is duplicated per application, hard-
deleted with no archive, and returned by default on every read. `Notes` and
`job_postings.Description` have the same shape. Nothing in the repo identifies these
as personal information. From Phase 4 onward the deployed path ships this content to
a third-party LLM (`phases/phase-4-ai-analyzer.md` step 4 swaps `IChatClient` off Ollama);
no doc records that consequence.

**F3 · No TLS requirement on the database connection.**
No `SSL Mode`, `Trust Server Certificate` or root-certificate keyword appears anywhere
in the repo. Npgsql 8 defaults to `SslMode=Prefer`: TLS is *opportunistic*, **silently
falls back to plaintext** if the server does not offer it, and certificates are not
validated. Against local Docker this is harmless. Against RDS it is a
credential-and-résumé disclosure with no signal that the downgrade happened.

**F4 · A tracked config file holds a plaintext DB password.**
`src/appsettings.Development.json:12` contains
`Host=localhost;...;Password=dev`, and `git ls-files` confirms the file is tracked.
`.gitignore:21` excludes only `appsettings.*.local.json`, so this pattern slips
through. The credential itself is a throwaway local Docker password also printed in
`README.md` and `CLAUDE.md` — **the risk is the pattern, not this value.** That file
is the obvious place a real Neon connection string lands in Phase 10, and it is already
tracked.

**F5 · GraphQL is not defended by `[JsonIgnore]`. (Verified against the emitted SDL.)**
**RESOLVED in Phase 2.3 — see the note after this finding.**
HotChocolate honours `[GraphQLIgnore]`, not `System.Text.Json`'s `[JsonIgnore]`, and
there is no `[GraphQLIgnore]` anywhere in `src/`. The back-references hidden from REST
are therefore **present in the published schema** — confirmed by reading the SDL from
a running instance:

```graphql
type Company    { ... postings: [JobPosting!]! }
type JobPosting { ... applications: [JobApplication!]! }
type Skill      { ... postingSkills: [PostingSkill!]! }
```

So this query is valid against the live schema:

```graphql
{ applications { posting { company { postings { applications { resumeText notes } } } } } }
```

It walks from any one record to **every résumé in the database**. It was submitted
against a running instance and **passed validation** — HotChocolate 14.3's `@cost`
directives did not reject it; only the downed database stopped execution. Combined
with F1, this was the most serious finding in the audit.

> **Resolved 2026-08-26 (Phase 2.3).** Every GraphQL root field now returns a
> response DTO instead of an EF entity. HotChocolate builds the schema from
> resolver return types, so `JobApplication`, `Company`, `Skill` and the rest are
> no longer *in* the published schema — the query above fails validation with an
> unknown-field error rather than executing. Worth noting **how** it was closed:
> not by adding `[GraphQLIgnore]` to each back-reference, which would have needed
> remembering on every future navigation property, but by removing the entities
> from the surface entirely. The regression guard is
> `SurfaceParityTests.NoEfEntityIsReachableFromTheGraphQLSchema`, which asserts
> against the emitted SDL — the same artefact this finding was made from.
>
> F1 (no authentication) is untouched. Every application is still readable by
> anyone who can reach the port; what changed is that reading one no longer hands
> you all of them.

**F6 · `GET /applications` is an unpaged full-table dump. RESOLVED in Phase 2.3.**
The endpoint returned every application with the entire eager-loaded object graph.
It is now paged (default 20, hard ceiling 100, both enforced in the handler for
REST and GraphQL alike) and projects to a summary DTO that carries neither
`Description` nor `ResumeText` — so the list is bounded *and* no longer a résumé
dump. Recorded here rather than deleted because the exposure framing is what made
it worth fixing early: A1 called it a performance problem.

The ceiling matters on an unauthenticated surface for its own reason —
`?pageSize=1000000` reached `Take()` directly before it existed.

### S2 — Medium: schema integrity

**F7 · No concurrency token. RESOLVED in Phase 7** — `xmin` shadow property on `job_applications`, `job_postings` and `companies`; zero added columns. Original finding follows.

**F7 (as written)** · No concurrency token → last-write-wins on the read-modify-write in
`Modules/Applications/UpdateApplication.cs`. Two concurrent PATCHes silently discard
one. (The code moved out of `PostgresJobApplicationRepository.UpdateAsync` in Phase
2.3; the read-modify-write, and this finding, moved with it unchanged.)

**F8 · The audit columns are inconsistent, and one of them already lies. RESOLVED in Phase 7** — `IAuditable` on the seven independently-lifecycled entities, maintained by `AuditSaveChangesInterceptor` so there is one write path rather than one per slice. `job_postings.UpdatedAtUtc` now exists. Note the boundary the fix adopted: the timestamp records when **that row** changed, not when anything beneath it did. Original finding follows.

**F8 (as written)** · The audit columns are inconsistent, and one of them already lies.

- `job_postings` has `CreatedAtUtc` but **no** `UpdatedAtUtc`, despite PATCH mutating
  `Title`, `Location`, `Description` and `CompanyId`.
- `companies`, `skills`, `posting_skills`, `job_requirements` have none at all.
- `job_applications.UpdatedAtUtc` is set by hand in exactly one place
  (`Modules/Applications/UpdateApplication.cs`, formerly
  `PostgresJobApplicationRepository.cs:75`). `AddSkillToPostingAsync` mutated the
  aggregate and saved **without touching it**.
- **Updated 2026-08-25, after Phase 2.1.** That method no longer exists — it became
  `Modules/Applications/AddSkillToPosting.cs`. The finding did not go with it: the
  phase added *four* slices that each `SaveChangesAsync`, and none maintains a
  timestamp. One stale write path was replaced by four. This is the finding
  demonstrating itself; see §6.

That last point is the argument for automating this rather than a style preference:
**the column is wrong today.** A hand-maintained audit column is only correct until
someone adds a second write path, which had already happened when this was written —
and happened four more times in the very next phase.

**F9 · No `CreatedBy` / `UpdatedBy` anywhere.** Blocked on F1 — there is no actor to
name yet.

**F10 · Hard deletes only. RESOLVED in Phase 8** — `ISoftDeletable` on the three
entities with a delete slice, converted centrally in `AuditSaveChangesInterceptor`
so `Remove()` means archive, plus `HasQueryFilter` and a restore route on both
surfaces. **The cascades named below no longer fire at all**, because no `DELETE`
reaches Postgres — which is what makes the restore whole. Note the finding's own
last sentence is now the wrong way round: nothing is *ir*recoverable, and the
remaining gap is the opposite one, that nothing is ever actually removed (F18).
Original finding follows.

**F10 (as written)** · Hard deletes only. `DeleteAsync` (lines 115-125) is a bare `Remove`; the
cascade rules then drop `ats_results`, and deleting a posting would drop its
`ai_analyses`, `job_requirements` and `posting_skills`. Nothing is recoverable.

**F11 · No DB-side defaults at all. RESOLVED in Phase 7** — `gen_random_uuid()` on every Guid PK and `now() at time zone 'utc'` on the audit timestamps, applied as a convention loop so a new table inherits it by existing. Original finding follows.

**F11 (as written)** · No DB-side defaults at all. No `gen_random_uuid()` on any PK, no `now()` on
any timestamp. Every id and timestamp originates in a C# property initializer, so any
writer that is not this EF application — a migration backfill, a `psql` fix, a future
service — violates the schema's own invariants without the schema noticing.

**F12 · No CHECK constraints. RESOLVED in Phase 7** — `ck_job_postings_salary_range` and `ck_job_postings_currency_iso4217`. Original finding follows.

**F12 (as written)** · No CHECK constraints. `SalaryMin <= SalaryMax` is unenforced.
`SalaryCurrency` is `varchar(3)` with no ISO-4217 validation, so `"XX!"` is accepted.

**F13 · Eleven unbounded `text` columns. RESOLVED in Phase 7** — all bounded. **The column list below is stale and was already stale before Phase 7**: it names `job_applications.ResumeText`, which Phase 4.5 deleted when the résumé moved to its own table. Eleven columns were bounded; not these eleven. Left uncorrected as an example of what "the standing docs lag between sweeps" looks like. Original finding follows.

**F13 (as written)** · Eleven unbounded `text` columns. Every column below is `type: "text"` with
no `HasMaxLength`, verified by line in `Migrations/20260819115119_InitialCreate.cs`:

| Table | Columns | Lines |
|---|---|---|
| `companies` | `Website`, `Industry`, `HqLocation` | 21-23 |
| `job_postings` | `Location`, `Description`, `SourceUrl` | 50, 56, 58 |
| `ai_analyses` | `Summary`, `ModelUsed` | 79-80 |
| `job_applications` | `Notes`, `ResumeText` | 102-103 |
| `job_requirements` | `Text` | 124 |

On an unauthenticated public write endpoint that is an unbounded-storage vector.
`ai_analyses.ModelUsed` is the clearest case that a bound is simply missing — it holds
a model identifier like `llama3.2:3b`, so `varchar(100)` is generous.

**F14 · No index backs the default query. RESOLVED in Phase 7** — `IX_job_applications_Status` and `IX_job_applications_DateApplied` (DESC, matching the sort). Original finding follows.

**F14 (as written)** · No index backs the default query. `ListApplicationsHandler` sorts on
`DateApplied` by default and filters on `Status`, `DateApplied`, and — through a
join — company and skill names. None of those columns is indexed; only the FKs are.
Phase 2.3 shipped the filtering and deliberately left the indexes to what is now **Phase 7** (written as "Phase 2.7" at the time) so
that phase stays one migration: at a few hundred personal rows the seq scan is
sub-millisecond, and an index added before the query pattern settles is a guess.
Still open, and now with a concrete query pattern to index *for*.

### S3 — Recorded / roadmap

**F15** No `audit_events` table — already in `backlog.md:61`, correctly sized as
"new table + write-path change on *every* mutation".

**F16** No status-history table — `phases/phase-2.5-status-rules.md:50` scopes it out
deliberately.

**F17** Domain attribute gaps vs schema.org (§2) — `validThrough`, `jobLocationType`,
`identifier` and source/channel are **not** currently in the backlog.

**F18** No backup, restore, retention or PII register in any doc. Phase 10's disposal
plan is *"tear it down after the job search"*, which is a cost decision standing in
for a data-lifecycle decision.

---

## 4. What the schema already gets right

Worth stating, because the section above is one-sided and these are defensible
choices:

- **Delete behaviour is deliberate, not default.** `companies → job_postings` and
  `job_postings → job_applications` are `RESTRICT`; the owned children
  (`posting_skills`, `job_requirements`, `ai_analyses`, `ats_results`) are `CASCADE`.
  The reasoning is written into `AppDbContext.cs` at each rule.
- **Enums stored as strings**, so raw rows are self-describing.
- **Explicit precision** on money (`numeric(12,2)`) rather than a float.
- **Unique natural keys** on `companies.Name` and `skills.Name`, backing the
  find-or-create dedup that the whole Postgres-over-DynamoDB decision rests on.
- **Composite PK** on `posting_skills` — the join table cannot hold a duplicate pair.
- **1:1 enforced by a unique index**, not by convention (`ai_analyses`, `ats_results`).

---

## 5. Remediation plan

Each step ends runnable, per priority 2 in `CLAUDE.md`. **Any step that moves the
schema must redraw `docs/diagrams/*.svg` in the same change**, using the
`schema-diagram` skill — it derives DDL from `dotnet ef migrations script`, which is
why it catches the column types and delete rules that reading `Models/*.cs` misses.

### Step 1 — Audit & integrity baseline → **Phase 7 (next)** *(one migration; no auth required)*

Closes F7, F8, F11, F12, F13, F14.

- `IAuditable { CreatedAtUtc, UpdatedAtUtc }` implemented by all 8 entities.
- **`AuditSaveChangesInterceptor`** (new, `src/Data/`), registered on `AddDbContext`.
  This is the fix for F8: one write path for the timestamps, so a second mutation
  method cannot silently skip them.
- DB-side defaults: `now()` on both timestamps, `gen_random_uuid()` on PKs.
- `UseXminAsConcurrencyToken()` on `job_applications`, `job_postings`, `companies`.
  Zero added columns.
- CHECK constraints, `HasMaxLength` on the eleven unbounded columns, and indexes on
  `DateApplied DESC` and `Status`.

### Step 2 — Soft delete → **Phase 8. DONE 2026-09-04.**

Closed F10. `IsDeleted` + `DeletedAtUtc` + `HasQueryFilter`.

**Two corrections the work made to the gotcha below, both recorded in
`phase-8-soft-delete.md`:** only `resumes.LabelNormalized` needed the filtered
index, because neither `companies` nor `skills` has a delete path; and the
predicate `HasQueryFilter` does **not** reach is the one in the three published
views, which are raw SQL and had to be re-cut in the migration by hand.

> **Gotcha — write this one down.** `companies.Name` and `skills.Name` are UNIQUE.
> Under soft delete they must become **filtered** unique indexes
> (`.HasFilter("\"IsDeleted\" = false")`) or soft-deleting a company permanently
> blocks ever re-adding that name — and the find-or-create dedup depends on those
> exact indexes.

### Step 3 — Owner scoping → **Phase 11** *(the big one; tied to the deploy)*

Closes F1, F9. A `users` table, and `OwnerUserId` on `job_applications`,
`job_postings` and `companies`.

> **Recommended scoping root: keep `skills` global.** It is shared reference data with
> no PII, and per-user skill rows would destroy the single `GROUP BY` that decision 1
> (Postgres over DynamoDB) and Phase 2.4 analytics both rest on. This is a real
> tradeoff — one user's skill taxonomy becomes visible in aggregate to another — and
> it is the right one for a schema whose reason to exist is cross-job skill analytics.

`CreatedBy`/`UpdatedBy` become nullable FKs to `users`. Enforcement is **an EF global
query filter *plus* Postgres RLS**: the filter makes the app naturally query one
user's rows; RLS makes a forgotten filter unable to leak.

### Step 4 — Deploy hardening → **Phase 10** *(config, not schema)*

Closes F3, F4.

- `SSL Mode=VerifyFull` plus the RDS root certificate.
- RDS `StorageEncrypted` with the **AWS-managed `aws/rds` key** — free-tier eligible,
  where a customer-managed key is not. **Decide before creating the instance:
  encryption cannot be enabled afterwards.**
- Untrack `appsettings.Development.json`, widen the `.gitignore` rule, adopt
  `dotnet user-secrets` for local overrides.
- Connection string in **SSM Parameter Store, standard tier (free)** rather than
  Secrets Manager (~$0.40/secret/month) — this matches priority 1. Record IAM database
  authentication as the passwordless upgrade path if the project ever justifies it.
- A least-privileged application role, not the RDS master user.
- Enable RDS automated backups.

### Step 5 — Privacy guardrail → **Phase 10** *(before `IChatClient` points off Ollama)*

Closes F2, F18. A PII register in `docs/`; an explicit decision on whether
`ResumeText` leaves the machine once `IChatClient` points at a hosted provider; and a
retention rule for `ResumeText` per APP 11.2.

### Later — `audit_events` (Tier 4), per `backlog.md`. **P4**, after Phase 11 — F9 needs an actor to name, and Phase 7's interceptor is the write-path hook this extends.

---

## 6. Why this is worth having as portfolio evidence

The finding to lead with is **F8**, not F1. "No auth on a single-user local tool" is a
scope decision any interviewer will accept. *"I found an audit column that was already
wrong, traced it to a second write path that skipped it, and replaced hand-maintenance
with an interceptor"* is a debugging story with a root cause and a structural fix.

It got a better ending in Phase 2.1. That phase deleted the exact method this finding
cited — and added four new write paths with the same gap, none of them thinking about
timestamps. The prediction ("only correct until someone adds a second write path") was
confirmed within one phase, by the same person who wrote it. That is a stronger answer
than the original: the fix has to be structural precisely because a careful author who
had *just documented the problem* still didn't hand-maintain the column.

**F5** is the second one: the point is not that GraphQL exposed a field, it is that
`[JsonIgnore]` **looked like** it protected both surfaces and only protected one — and
that this was confirmed by reading the emitted SDL rather than by trusting the
annotation. That is the same reasoning as the `schema-diagram` skill's rule about
deriving from EF rather than from the model classes, applied to a different artefact.

Its resolution in Phase 2.3 adds a second half worth telling: the fix was not
`[GraphQLIgnore]` on each back-reference, which would have to be remembered on every
navigation property added afterwards. Returning DTOs took the entity types out of the
schema, so the walk is impossible rather than merely blocked — and the fix arrived as
a *side effect* of a phase about paging. The mechanism that caused the finding
(HotChocolate infers the schema from return types) is the same one that closed it.

**Step 3's tradeoff** is the third: choosing to leave `skills` global, naming the
privacy cost of that, and tying it back to the reason Postgres was chosen at all.

---

## Sources

- [OWASP Database Security Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Database_Security_Cheat_Sheet.html)
- [OWASP Cryptographic Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html)
- [Row Level Security for Tenants in Postgres — Crunchy Data](https://www.crunchydata.com/blog/row-level-security-for-tenants-in-postgres)
- [EF Core 8 Multi-Tenancy: Tenant Isolation, Soft Deletes, Audit Trails & Query Filters](https://www.dotnet-guide.com/tutorials/ef-core/multitenancy-softdelete-auditing/)
- [EF Core `SaveChangesInterceptor` for auditing entities](https://mehmetozkaya.medium.com/ef-core-interceptors-savechangesinterceptor-for-auditing-entities-in-net-8-microservices-6923190a03b9)
- [Encryption best practices for Amazon RDS — AWS Prescriptive Guidance](https://docs.aws.amazon.com/prescriptive-guidance/latest/encryption-best-practices/rds.html)
- [Amazon Aurora PostgreSQL / RDS for PostgreSQL Security Whitepaper](https://d1.awsstatic.com/Amazon%20Aurora%20PostgreSQL%20and%20Amazon%20RDS%20for%20PostgreSQL%20Security%20Whitepaper.pdf)
- [schema.org `JobPosting`](https://schema.org/JobPosting)
- [Employee data under the Australian Privacy Act](https://securiti.ai/blog/employee-data-australia/)
- [Data retention in Australia](https://www.infocentric.com.au/2026/01/12/beyond-the-breach-data-retention-in-australia/)
- [Soft deletion probably isn't worth it — brandur.org](https://brandur.org/soft-deletion) — the dissenting view, cited because step 2 accepts a cost this argues against
