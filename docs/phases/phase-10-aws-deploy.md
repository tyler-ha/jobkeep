# Phase 10 — Deploy to AWS Lambda (Function URL)

> **Formerly "Phase 3".** Renumbered 2026-09-01 into build order — the plan was
> complete and parked while phases 4, 4.5, 5 and 6 were built past it, so a lower
> number no longer meant earlier. Nothing about the plan below changed. Older
> *done* phase docs and `src/` comments written before the renumber still say
> "Phase 3"; those are dated records and were deliberately not rewritten. See
> `architecture.md` decision 18.

**Status: DROPPED (2026-09-04), at the user's instruction. AWS is not the
deployment target. Everything below is kept as a researched record, not a plan.**

> **Dropped, not parked.** Parked meant "unpark when there is a reason to click
> the link"; the decision on 2026-09-04 was that the target itself is wrong —
> *"we are going to drop the AWS deploy, we gonna use different free tools later
> on."* So this doc stops being a queue item.
>
> **What survives the drop, and is worth not re-deriving when a host is picked:**
>
> - **The rule this phase produced still stands: nothing in the deployed
>   architecture may bill per hour.** It was never an AWS rule. It is what
>   rejected RDS, Aurora Serverless v2 and a NAT Gateway below, and it is the
>   test any replacement host has to pass.
> - **Neon's free tier was chosen on its own merits** — serverless Postgres,
>   scales to zero, $0, no clock — and nothing about it was AWS-specific. It
>   remains the leading candidate for the database wherever the API ends up.
> - **The research below is still an answer to "why not X".** The Aurora and
>   API Gateway rejections, the cold-start numbers and the "this account has no
>   free tier left" finding are dated facts, not opinions. Read them before
>   proposing an AWS variant again.
> - **The container is the portable half.** `src/Dockerfile` and `compose.yaml`
>   already build and run the API without any AWS-specific hosting code, which
>   is why dropping the target costs no source changes. Whatever host is chosen
>   (Fly.io, Railway, Render, Azure Container Apps, a small VPS) starts from
>   that image, and only a Lambda entry point would have been thrown away —
>   it was never written.
>
> Two things that were due "when the deploy unparks" now have no trigger and
> need a new one: the **doc/security-audit sweep** and the audit's **transport &
> secrets hardening**. Both are re-scheduled to *before whatever deploy replaces
> this one*. See `architecture.md` decision 22.

> **Rewritten 2026-08-26**, then **revised 2026-08-27** after a pricing/latency
> research pass. The original plan targeted API Gateway in front of Lambda, with
> PostgreSQL on the RDS 12-month free tier; both premises expired. The 2026-08-26
> rewrite replaced them with a Function URL and Aurora Serverless v2 at 0 ACU.
> **The Function URL survived the research; Aurora did not** — see "Why Aurora was
> dropped". Phase 2.6 (.NET 10) is **done**, so the prerequisite is cleared.

## Goal

Get a real, live URL — the actual "shipped to the cloud" milestone for the
portfolio — for **effectively $0/month**, on an AWS account that has **no free
tier left**.

## The constraint this phase is designed around

The account is older than 12 months, so the legacy free tier is spent and the
post-July-2025 credits model does not apply to existing customers. What remains
is the **always-free** tier, which has no clock on it:

| Service | Always free? | Consequence for this phase |
|---|---|---|
| **Lambda** | **Yes** — 1M requests + 400,000 GB-seconds/month, permanently | Compute is $0 forever at this traffic |
| **Lambda Function URL** | **Yes** — no per-request charge | This is why API Gateway is gone |
| API Gateway | **No** — 1M calls/month for 12 months only, then $1.00/M (HTTP) | Dropped |
| RDS | **No** always-free tier | Dropped |
| Aurora Serverless v2 | No, but bills **per second from 0 ACU** | ~$0.10–1/month idle — but see "Why Aurora was dropped" |

**The rule that came out of the research: nothing in this architecture may bill
per hour.** Every near-$0 stack that works has the same shape — scale-to-zero
compute on a permanent free grant, plus a database that is *someone else's*
problem to keep at zero. Per-hour line items (a NAT Gateway, an interface
endpoint, RDS Proxy, a provisioned instance) are what turn $0 into $40, and they
arrive as a *consequence* of an architectural choice rather than as a decision
anyone makes on purpose.

## Plan

1. Add `Amazon.Lambda.AspNetCoreServer.Hosting` and a `LambdaEntryPoint`
   wrapping the existing `Program.cs`. **No slice, module or endpoint changes** —
   the whole point of the shape chosen in Phases 2.1-2.3 is that this is an
   adapter, not a rewrite.
2. Deploy with AWS SAM or the `dotnet lambda` CLI:
   ```bash
   dotnet tool install -g Amazon.Lambda.Tools
   dotnet lambda deploy-serverless
   ```
3. **Expose it with a Lambda Function URL, not API Gateway.** One fewer service,
   no 12-month clock, and no per-request charge.
4. **Database: Neon free tier (serverless PostgreSQL).**
   - **$0/month**: 0.5 GB storage and 100 compute-hours per project per month,
     up to 100 projects. This schema is far under 0.5 GB and this traffic is far
     under 100 CU-hours. No card required.
   - It is **real PostgreSQL**, so EF Core, Npgsql, the migrations, the foreign
     keys and the `text[]` columns all work unchanged. Point
     `ConnectionStrings__Postgres` at it; **no code change**.
   - Scale-to-zero after 5 minutes idle, and it **resumes in ~500ms** — the
     number that made this the pick rather than the fallback.
   - **The Lambda stays out of a VPC.** Neon is reached over the public internet
     with TLS, so there is no VPC, no NAT Gateway, no interface endpoint, and
     Phase 4's outbound AI call costs nothing. This is the real reason for the
     choice; the price is a consequence, not the argument.
   - Transport hardening lands here instead of at cluster creation: require
     `sslmode=require` (Neon enforces TLS anyway) and keep the connection string
     out of source — Lambda environment variables at minimum, SSM Parameter
     Store (free tier, standard parameters) if it gets shared. This is the
     audit's "transport & secrets hardening" item.
5. **Connections still need care, but the problem changed shape.** With Aurora, a
   held-open pooled connection kept the *bill* alive; with Neon it only delays
   scale-to-zero inside a free grant this traffic cannot exhaust. What matters
   now is not exhausting the *connection limit* from concurrent cold Lambdas.
   - Use Neon's **pooled connection string** (PgBouncer, built in). Neon
     documents this path for Lambda specifically.
   - Keep the Npgsql pool small and short-lived; a Lambda that is about to be
     frozen should not be holding many connections.
   - Cap **reserved concurrency** on the function. It bounds both connection
     count and the worst-case bill, and replaces the API Gateway throttling the
     old plan relied on.
   - **RDS Proxy stays rejected**, and now for two reasons rather than one: at
     ~$0.015/vCPU-hour (~$11/month) it cost more than the database it protected,
     *and* an attached RDS Proxy holds a connection to every instance in the
     cluster, which prevents Aurora auto-pause outright. It defeats the thing it
     was being considered to support.
6. **Auth moves into the app.** The old plan used an API Gateway API key; with a
   Function URL the choices are `AWS_IAM` (awkward for a browser demo) or
   `NONE`. Add a simple API-key check as middleware so the rule lives in one
   place and covers REST *and* GraphQL — the same "one rule, one implementation"
   principle the slices follow.
7. Apply EF migrations as a **deliberate release step**. The app only
   auto-migrates in Development, which is already correct; don't change it.
8. Guardrails before the first deploy: an **AWS Budget at $1** with an email
   alert (first two budgets are free), and a documented teardown command.

## Cost model

| Item | Monthly |
|---|---|
| Lambda (always-free tier) | **$0.00** |
| Function URL | **$0.00** |
| Neon — storage and compute, inside the free grant | **$0.00** |
| CloudWatch Logs (a few MB, inside the 5 GB always-free tier) | **$0.00** |
| **Total** | **$0.00** |

Nothing here bills per hour, which is the property worth stating out loud. The
first line item that would appear is CloudWatch Logs past 5 GB/month, which this
traffic cannot reach.

**Documented fallback:** Aurora Serverless v2 at 0 ACU, which was the 2026-08-26
pick. It costs ~$0.10–1/month for storage plus $0.20 per million I/O, keeps the
"entirely on AWS" line in the story, and requires accepting the wake latency and
the VPC consequences below. Switching is a connection-string change plus VPC
wiring — worth knowing, not worth doing pre-emptively.

## Why Aurora was dropped

Two facts, both verified against AWS's own documentation, and both missing from
the 2026-08-26 version of this plan:

- **Wake latency is 15 seconds, and 30+ seconds after a day idle.** AWS documents
  ~15s as the typical resume, and says that an instance paused for more than 24
  hours goes into a deeper sleep whose resume is "roughly equivalent to doing a
  reboot of the instance". A portfolio link is clicked once a fortnight, so it
  would live permanently in that deeper sleep. Stacked on a ~2s .NET Lambda cold
  start, the first request is **~30–35 seconds** — past what a browser default
  waits for and well past what a recruiter does.
- **Aurora is VPC-private, so the Lambda must join a VPC — and Phase 4 is AI
  calls.** A VPC-attached Lambda has no route to the internet without a **NAT
  Gateway (~$32–43/month)** or a per-service **interface endpoint (~$7/month)**.
  The deploy alone would not have paid it; Phase 4 would, and by then the choice
  would already be made. The $0.10/month figure was true for exactly one phase.

The 0-ACU reasoning itself was sound and is kept as the fallback. What the
research changed is which side of the tradeoff this project is on: for a demo URL
whose whole job is to be fast the *first* time a stranger opens it, 30 seconds is
the wrong currency to pay in.

## What comparable stacks do

Everything credible at this price point converges on the same two-part shape.
What varies is who runs the database and which cold start you accept.

| Stack | Idle cost | First-request wake | Why not chosen here |
|---|---|---|---|
| **Lambda Function URL + Neon** | **$0** | ~2s (.NET) + ~0.5s | **This is the pick.** |
| Lambda + Aurora Serverless v2 @ 0 ACU | ~$0.10–1 | 15s, 30s+ after 24h idle | Wake latency and the VPC/NAT consequence. Kept as fallback. |
| Lambda + Supabase free tier | $0 | manual | 500 MB free, but free projects **pause after 7 days idle** and need a console click to wake. Strictly worse than Aurora for a link that sits untouched. |
| Google Cloud Run + Neon | $0 | ~2s | Genuinely comparable — 2M requests, 180k vCPU-s, 360k GiB-s always free per *account*. Rejected only because it is a second cloud for no extra signal. |
| Azure Container Apps + Neon | $0 | ~2s | Same free-grant shape, scale-to-zero to 0 replicas, and the best Melbourne-market signal of the three. The intended shape is a *later* "same container, second cloud" phase — doing it now costs a phase to re-prove this one. Not scheduled, and deliberately not added to `backlog.md` yet. |
| Oracle Cloud Always Free VM (Docker + Postgres) | $0 | **none** | The only $0 option with no cold start at all. Rejected: it hands back every piece of ops work serverless removes, and see "Deliberately not done here" for what Oracle did to the tier in June 2026. |
| Fly / Render / Railway | $5–25 | ~0 | Not $0. Render also cut Hobby egress to 5 GB in April 2026. |

The generalisable finding, and the one worth being able to say out loud: **nobody
running at $0 keeps the database inside their own VPC.** The VPC boundary is what
forces a per-hour line item, whichever cloud you are on.

## Accept the cold start; don't hide it

A .NET Lambda that has been idle ~10 minutes can take **over 2 seconds** to
answer, and Neon adds roughly half a second resuming. The first request after a
quiet period will be noticeably slow — call it **~2.5–3 seconds**, against the
~30 seconds the Aurora version would have cost.

The two ways to remove even that were both refused:

- **SnapStart is not free for .NET.** It is free only for Java managed runtimes;
  .NET pays a snapshot-caching charge *and* a per-restore charge.
- **Native AOT** gets sub-100ms starts and costs nothing — but it fights EF
  Core's reflection hard, and EF Core with a real relational model is the
  central claim of Phase 2. Not worth breaking to save a second.

So: put a line in the root `README.md` saying the first request wakes the
function and the database. "I chose scale-to-zero, measured the cold start, and
here is what the alternatives would have cost" is a better answer in an interview
than a warm server nobody asked about.

## Why the plan changed

- **The RDS free tier no longer applies.** AWS replaced the free tier on
  15 July 2025; existing customers are explicitly ineligible for the new credits,
  and this account's legacy 12 months are spent.
- **API Gateway was never always-free** — 1M calls/month for 12 months, then
  $1.00/M for HTTP APIs. At this traffic the bill is pennies, but a Function URL
  is $0 and one service simpler.
- **Lambda's free tier genuinely is permanent** — 1M requests and 400,000
  GB-seconds a month, with no 12-month clock, and it applies to accounts opened
  before the July 2025 change. This is the one load-bearing assumption left in
  the phase, so it was checked rather than assumed.
- **Aurora's wake latency and VPC consequence** (see above) moved the database
  off AWS.
- **AWS App Runner closed to new customers in April 2026**, so the "just run the
  container" fallback on AWS is now ECS Express Mode, not App Runner. Worth
  knowing before it is needed.

## Deliberately not done here

- **A pivot to Azure.** Melbourne .NET listings lean slightly Azure, but the
  recurring wording is "Azure PaaS … or AWS/Google cloud equivalent" — employers
  are testing whether managed services are understood, not which console has been
  clicked. Switching now costs a phase for a marginal signal. The better version
  of that idea is a *later* phase that deploys the same container to Azure
  Container Apps and writes up what changed and what didn't. That is intent, not a
  scheduled item.
- **An Oracle Cloud Always Free VM.** It is the only way to get $0/month with no
  cold start, and it is a real option people use. Rejected on ops burden, and on
  a concrete demonstration that a free tier is a vendor's promise rather than an
  architecture: on **15 June 2026 Oracle halved the Ampere A1 always-free
  allocation** to 2 OCPU / 12 GB and began terminating over-limit instances from
  **18 August 2026**, and it reclaims instances judged idle over a 7-day window —
  which a job tracker with one user certainly is.
- **A paid PaaS or a VPS.** Railway, Render and Fly all now start around
  $5–25/month, and Render cut Hobby egress to 5 GB in April 2026. A Hetzner box
  with Docker and Caddy is genuinely cheaper at scale and would make the demo feel
  instant — but it trades priority 1 for convenience this project does not need at
  zero traffic, and it hands back the ops work.

## Interview talking points from this phase

- Reading a pricing page as a design input: Function URL over API Gateway, and
  managed Postgres outside the VPC over Aurora inside it, are both cost decisions
  with an architectural consequence.
- **The VPC boundary is the cost boundary.** The moment the database is private,
  the function joins a VPC, and the next feature that calls an external API pays
  a NAT Gateway. The $0 floor is set by a network decision, not a database one —
  and Phase 4 is what would have exposed it.
- Scale-to-zero and connection lifetime are the *same* problem — a pooled
  connection is what stops a scale-to-zero database from actually reaching zero.
- Rejecting RDS Proxy twice over: it cost 100x the thing it protected, *and* it
  would have prevented the auto-pause it was meant to support.
- Knowing that SnapStart's pricing differs by runtime, and choosing the slow-path
  tradeoff deliberately rather than by default.
- Treating a free tier as a revocable promise: PlanetScale removed its free tier
  in 2024, Oracle halved its always-free compute in June 2026. The mitigation is
  that Neon is plain Postgres, so moving to Aurora or RDS is a connection-string
  change — the *portability* is the design decision, not the vendor.

## Sources for the numbers above

Recorded so a later session does not re-research them.

- [Aurora auto-pause — resume latency, deep sleep, conditions that prevent pausing](https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/aurora-serverless-v2-auto-pause.html)
- [Aurora Serverless v2 scaling to zero (announcement)](https://aws.amazon.com/about-aws/whats-new/2024/11/amazon-aurora-serverless-v2-scaling-zero-capacity)
- [AWS Lambda pricing — always-free tier](https://aws.amazon.com/lambda/pricing/)
- [Neon pricing](https://neon.com/pricing) and [Connect from AWS Lambda](https://neon.com/docs/guides/aws-lambda)
- [Cloud Run pricing](https://cloud.google.com/run/pricing) · [Azure Container Apps pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/)
- [OCI Always Free resources](https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm) and the [June 2026 limit reduction](https://oracommit.blogspot.com/2026/02/understanding-oci-always-free-compute.html)
- [NAT Gateway pricing](https://cloudburn.io/blog/aws-nat-gateway-pricing)

## Next

Phase 4 — add the AI job-description analyzer.
