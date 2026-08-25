# Security & data-structure audit

**Status:** Audit complete 2026-08-25. Remediation **not started** — see §5.
**Scope:** The 8-table PostgreSQL schema as of migration `20260819115119_InitialCreate`,
plus the config and API surfaces that expose it.
**Method:** Schema read from `AppDbContextModelSnapshot.cs` (the DDL ground truth),
not from `Models/*.cs`. GraphQL exposure verified against the emitted SDL from a
running instance, not inferred from attributes.

This document exists because of the standard set in `CLAUDE.md`: *"Write down the
tradeoff."* Authentication is deferred **legibly** — it is recorded in `backlog.md`,
in the gap register, and tied to Phase 3+. Encryption, PII handling, retention,
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
load-bearing at Phase 3**, which puts this schema on a public API Gateway in front of
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

### S1 — High: resolve before the Phase 3 deploy

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
is the obvious place a real RDS connection string lands in Phase 3, and it is already
tracked.

**F5 · GraphQL is not defended by `[JsonIgnore]`. (Verified against the emitted SDL.)**
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
with F1, this is the most serious finding in the audit.

**F6 · `GET /applications` is an unpaged full-table dump.**
`Endpoints/ApplicationEndpoints.cs:28-32` returns every application with the entire
eager-loaded object graph (`PostgresJobApplicationRepository.WithGraph()`, lines
22-29). Already recorded as A1/A2; noted here because it is also the data-exposure
path, not only a performance one.

### S2 — Medium: schema integrity

**F7 · No concurrency token** → last-write-wins on the read-modify-write in
`PostgresJobApplicationRepository.UpdateAsync` (lines 52-78). Two concurrent PATCHes
silently discard one.

**F8 · The audit columns are inconsistent, and one of them already lies.**

- `job_postings` has `CreatedAtUtc` but **no** `UpdatedAtUtc`, despite PATCH mutating
  `Title`, `Location`, `Description` and `CompanyId`.
- `companies`, `skills`, `posting_skills`, `job_requirements` have none at all.
- `job_applications.UpdatedAtUtc` is set by hand in exactly one place
  (`PostgresJobApplicationRepository.cs:75`). `AddSkillToPostingAsync` mutated the
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

**F10 · Hard deletes only.** `DeleteAsync` (lines 115-125) is a bare `Remove`; the
cascade rules then drop `ats_results`, and deleting a posting would drop its
`ai_analyses`, `job_requirements` and `posting_skills`. Nothing is recoverable.

**F11 · No DB-side defaults at all.** No `gen_random_uuid()` on any PK, no `now()` on
any timestamp. Every id and timestamp originates in a C# property initializer, so any
writer that is not this EF application — a migration backfill, a `psql` fix, a future
service — violates the schema's own invariants without the schema noticing.

**F12 · No CHECK constraints.** `SalaryMin <= SalaryMax` is unenforced.
`SalaryCurrency` is `varchar(3)` with no ISO-4217 validation, so `"XX!"` is accepted.

**F13 · Eleven unbounded `text` columns.** Every column below is `type: "text"` with
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

**F14 · No index backs the default query.** `GetAllAsync` sorts
`OrderByDescending(a => a.DateApplied)` and Phase 2.3 will filter on `Status`.
Neither column is indexed; only the FK columns are.

### S3 — Recorded / roadmap

**F15** No `audit_events` table — already in `backlog.md:61`, correctly sized as
"new table + write-path change on *every* mutation".

**F16** No status-history table — `phases/phase-2.5-status-rules.md:50` scopes it out
deliberately.

**F17** Domain attribute gaps vs schema.org (§2) — `validThrough`, `jobLocationType`,
`identifier` and source/channel are **not** currently in the backlog.

**F18** No backup, restore, retention or PII register in any doc. Phase 3's disposal
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

### Step 1 — Audit & integrity baseline *(one migration; no auth required)*

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

### Step 2 — Soft delete *(`backlog.md`'s "strongest candidate to pull in")*

Closes F10. `IsDeleted` + `DeletedAtUtc` + `HasQueryFilter`.

> **Gotcha — write this one down.** `companies.Name` and `skills.Name` are UNIQUE.
> Under soft delete they must become **filtered** unique indexes
> (`.HasFilter("\"IsDeleted\" = false")`) or soft-deleting a company permanently
> blocks ever re-adding that name — and the find-or-create dedup depends on those
> exact indexes.

### Step 3 — Owner scoping *(the big one; tie to Phase 3)*

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

### Step 4 — Phase 3 hardening *(config, not schema)*

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

### Step 5 — Privacy guardrail *(before Phase 4 deploys off Ollama)*

Closes F2, F18. A PII register in `docs/`; an explicit decision on whether
`ResumeText` leaves the machine once `IChatClient` points at a hosted provider; and a
retention rule for `ResumeText` per APP 11.2.

### Later — `audit_events` (Tier 4), per `backlog.md:61`. Own phase.

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
