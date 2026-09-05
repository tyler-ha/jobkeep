using System.Text.Json.Serialization;
// Phase 13.5: the Map*Module extensions 13.1 parked in Api/Endpoints/ are gone,
// replaced by the controllers in Api/Controllers/. The Add*Module() half still
// lives with its module and still does what a mediator cannot know about.
using Jobkeep.Api;
using Jobkeep.Api.Controllers;
using Jobkeep.Api.GraphQL;
using Jobkeep.Contracts.Applications;
using Jobkeep.Contracts.Skills;
using Jobkeep.Modules.Ai;
using Jobkeep.Modules.Analytics;
using Jobkeep.Modules.Applications;
using Jobkeep.Modules.Applications.Domain;
using Jobkeep.Modules.Match;
using Jobkeep.Modules.Documents;
using Jobkeep.Modules.Identity;
using Jobkeep.Modules.Skills;
using Jobkeep.Persistence;
using Jobkeep.Modules.Identity.Domain;
using Jobkeep.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Storage is PostgreSQL via EF Core. The connection string comes from config:
// appsettings.Development.json points at the local Docker container; a deployed
// environment supplies it via an environment variable instead — so local vs cloud
// is a config change, not a code change. (The deploy, Phase 10, is parked, and no longer names
// RDS: the plan it holds is Neon over the public internet, which is still just a
// connection string to everything below this line.)
var connectionString = builder.Configuration.GetConnectionString("Postgres");

// Phase 7 — the audit-timestamp interceptor (F8). Registered here rather than
// inside a context, because a context's job is the schema, in one readable
// place, and because a test needs to be able to swap the clock or leave the
// interceptor off entirely to write a known timestamp and watch it change.
//
// Singleton: it holds a clock function and nothing else, so there is no state to
// scope. AddDbContext is scoped, and a singleton dependency inside a scoped
// service is the safe direction — the captive-dependency hazard runs the other
// way.
// PHASE 11.2b — SCOPED, not singleton. It now stamps IOwned.OwnerUserId as
// well as the two timestamps, so it depends on ICurrentUser, which is per
// request. The AddDbContext factory below already resolves it from the scoped
// provider, so the change is one word here and nothing at the call site.
builder.Services.AddScoped<AuditSaveChangesInterceptor>();

// PHASE 11.2b — who is asking, for the five module contexts and the interceptor.
// Scoped, so one request is one answer; IHttpContextAccessor is how a plain
// class library reaches the principal without referencing ASP.NET itself.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

// ---------------------------------------------------------------------------
// PHASE 13.3b — six contexts, six schemas, six migration histories
// ---------------------------------------------------------------------------
// This block used to register ONE AppDbContext and then six interfaces over it,
// each resolving that same scoped instance. It bought the 13.2 property — a
// handler cannot name another module's table — while leaving Postgres untouched,
// so nothing about behaviour changed in that step. This is the step that changes
// it.
//
// What is the SAME: one connection string, one database, one Postgres server.
// Nothing here is a second deployment, and nothing is meant to be yet.
//
// What is DIFFERENT, and it is the whole point:
//
//   * Each context maps only its own module's tables, in its own SCHEMA. A
//     query cannot join across a boundary any more, because the other table is
//     not in the model. That is the property that survives extraction: a join
//     that does not exist cannot stop working when the boundary becomes a
//     network.
//
//   * Each has its own __EFMigrationsHistory, in its own schema. Five contexts
//     own tables and therefore migrations; Analytics owns neither. Separate
//     histories mean a module's schema can be created, migrated and eventually
//     MOVED without asking the other five for permission — which is exactly what
//     extracting one would require.
//
//   * They are six units of work. Two of them in one handler is two change
//     trackers and two transactions. Nothing in src/ holds two, but
//     ISkillCatalog.FindOrCreateAsync is called from four modules and saves
//     through the Skills context, so its "call me before adding rows of your
//     own" ordering rule is now load-bearing rather than precautionary.
//     CommitImport.CommitResumeAsync already gets it right and says why.
//
// The interceptor goes on all six. It stamps CreatedAtUtc/UpdatedAtUtc on
// anything IAuditable, which is a rule about entities rather than about modules,
// so leaving it off one context would be a silent inconsistency in the data
// rather than a visible one in the code.
void AddModuleContext<TContext>(string schema) where TContext : DbContext =>
    builder.Services.AddDbContext<TContext>((sp, options) => options
        .UseNpgsql(connectionString, npgsql => npgsql
            // Same table name in six schemas, rather than six names in one. A
            // module that is lifted out takes its schema whole, history included,
            // and needs no rename on the way.
            .MigrationsHistoryTable("__EFMigrationsHistory", schema))
        .AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>())

        // PHASE 8 — one model-validation warning, suppressed with its reason.
        //
        // EF warns that JobPosting has a query filter while PostingSkill and
        // JobRequirement, on the required end of a relationship with it, do not.
        // Its concern is real in general: query those child tables directly and
        // you get rows whose required parent the filter would have hidden.
        //
        // It cannot happen here. Every slice that touches either table resolves
        // its posting id out of `job_applications` first, and THAT read carries
        // the filter — so an archived application 404s before any child row is
        // reached, and a posting cannot be archived while a live application
        // names it. The path the warning describes does not exist.
        //
        // The remedy EF suggests — matching filters on both children — was
        // measured against that and refused: it would add an EXISTS back to
        // job_postings on every read of either table, including inside the skill
        // filter and the per-row skill projection in ListApplications, which is
        // this app's busiest query. Paying a join on the hot path to close a
        // hole nothing can reach is the wrong trade.
        //
        // ponytail: the safety rests on "every child slice routes through an
        // application". A future route addressed by posting id — an ad editor,
        // say — would break it silently. Add the two filters then, or check the
        // posting explicitly in that slice.
        .ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)));

AddModuleContext<ApplicationsDbContext>("applications");
AddModuleContext<SkillsDbContext>("skills");
AddModuleContext<DocumentsDbContext>("documents");
AddModuleContext<AiDbContext>("ai");
AddModuleContext<MatchDbContext>("ats");

// PHASE 11.1a — the sixth migrating context. It takes the same registration as
// the other five, including the audit interceptor, which is a deliberate no-op
// here: none of Identity's seven entity types is IAuditable or ISoftDeletable,
// so the interceptor sees nothing to stamp. Registering it anyway keeps "every
// module context is built the same way" true, which is worth more than saving
// one delegate on a context that will never trigger it.
AddModuleContext<IdentityDbContext>("identity");

// Analytics reads three views published by Applications and owns no tables, so
// it gets a context but no migrations history — there is nothing for it to
// create, and giving it a history table would imply otherwise.
builder.Services.AddDbContext<AnalyticsDbContext>((sp, options) => options
    .UseNpgsql(connectionString));

// ---------------------------------------------------------------------------
// PHASE 13.4 — dispatch
// ---------------------------------------------------------------------------
// One call registers every IRequestHandler<,> and INotificationHandler<> in the
// referenced module assemblies, and it is the ONLY registration either surface
// needs to reach a use case. Each module's Add*Module() below used to list its
// own handlers by name; what those calls still register is everything a mediator
// cannot know about — the contract implementations, the DbContexts, and the
// module-specific options.
//
// It also replaces the hand-rolled DomainEventPublisher 13.3c registered here.
// That file is deleted: IPublisher and INotificationHandler<> do the same job,
// and the two publish call sites did not move, which is exactly why the seam was
// hand-rolled a step early rather than waited for.
//
// martinothamar/Mediator is SOURCE-GENERATED. The registrations and the
// request-to-handler switch are emitted at compile time into this assembly, so
// Send() is a direct call rather than a reflection lookup — which is also what
// keeps the Lambda's trimming/AOT option open at Phase 10. That is why the
// generator package sits in Jobkeep.Api alone while the marker interfaces the
// modules implement come from Mediator.Abstractions, pinned once in
// Jobkeep.Contracts.
//
// What the generator does NOT buy: Send() takes IRequest<T>, so a request whose
// handler is missing compiles fine and throws MissingMessageHandlerException at
// runtime. That is the coupling the old `XHandler handler` parameter made the
// compiler check, and it is bought back deliberately in
// tests/Jobkeep.Tests/Architecture/DispatchTests.cs rather than assumed.
//
// Scoped, matching the six DbContexts a handler holds. The library's default is
// SINGLETON, and taking it would make every handler a captive dependency over a
// disposed context — the trap every AddScoped comment in this file names.
builder.Services.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Scoped);

// Every use case is a vertical slice under Modules/ (docs/architecture.md §2).
// Each slice handler takes its module's own DbContext directly, so this
// registers them and nothing else — as of Phase 2.3 there is no repository layer
// left to register.
builder.Services.AddApplicationsModule();

// Phase 13.2. The shared skill taxonomy, promoted out of Applications a step
// early because ISkillCatalog needs an owner. No routes: a skill is never the
// thing a user asks for (SkillsModule.cs).
builder.Services.AddSkillsModule();

// Phase 2.4. Read-only: three aggregate queries and no tables of its own. Since
// Phase 13.2 it reads three PUBLISHED VIEWS rather than Applications' tables, so
// the decision-13 exception it relied on is retired — Jobkeep.Contracts'
// PublishedViews.cs has the argument. Since 13.3b those views live in the
// `applications` schema and AnalyticsDbContext maps nothing else.
builder.Services.AddAnalyticsModule();

// Phase 4. Owns `ai_analyses`; reaches Applications-owned tables through
// IPostingContract because it *writes* to them, which the read-only exception
// Analytics uses does not cover (Modules/Applications/PostingContract.cs).
// Takes IConfiguration because the model endpoint and tag are config, not code —
// that is the whole point of putting the analyzer behind IChatClient.
builder.Services.AddAiModule(builder.Configuration);

// The language model client itself, shared by every module that wants one.
// Registered here rather than inside AddAiModule since Phase 4.5, because
// Documents also calls a model and the Ai module owns a table, not a technology
// (Shared/ModelClient.cs has the full argument).
builder.Services.AddModelClient(builder.Configuration);

// Phase 4.5. Owns `document_imports` and the four resume tables. Turns an
// uploaded PDF/DOCX/text file into a draft, and — only once a human confirms it —
// into real rows. Since 13.2c it names no other module: Applications is reached
// through IApplicationContract and the shared taxonomy through ISkillCatalog,
// both in Jobkeep.Contracts, and its csproj carries no module reference at all.
builder.Services.AddDocumentsModule(builder.Configuration);

// Phase 5. Owns `match_results` and, since 13.2e, names nothing else. It used to
// read five tables it did not own; all five are now contract calls, which is what
// MatchModule.cs argues at length. Still no IConfiguration: the two limits this
// module imposes on its one model call are constants rather than settings.
builder.Services.AddMatchModule();

// ---------------------------------------------------------------------------
// PHASE 11.1b — authentication
// ---------------------------------------------------------------------------
// AddIdentityApiEndpoints is the platform's own register/sign-in surface. It
// wires AddIdentityCore, both authentication schemes (a bearer token and the
// application cookie) and the endpoints MapIdentityApi maps further down.
//
// It is a DELIBERATE exception to 13.5's "every route is a controller action".
// Those routes are not written here — they are the framework's — and re-typing
// them as a controller would mean re-typing password hashing, the security
// stamp, lockout and the token flows, which is precisely the work decision 3
// chose this package to avoid. The rule 13.5 actually bought is "the composition
// root has one shape for HTTP", and one framework-supplied group beside
// MapControllers does not break it. A rule that has to be re-argued to add
// hand-written routes has done its job.
//
// ROLES ARE NOT ADDED (.AddRoles<IdentityRole<Guid>>()). The three role tables
// exist — 11.1a created them so that adding roles later is not a migration
// against a table that already has rows — but nothing authorizes on a role yet,
// so a RoleManager nobody injects would be registration for its own sake. The
// store resolves to a user-only store as a result, which is what the tables
// being empty already says.
//
// THE COOKIE'S SameSite IS LEFT AT THE DEFAULT, Lax, AND THAT IS A DECISION WITH
// AN EXPIRY DATE. Lax blocks a cookie on a cross-SITE request, and the front end
// at :5173 calling this at :5080 is cross-ORIGIN but same-site — SameSite is
// blind to the port, so the browser sends it and 11.1c works. A deployed front
// end on a different domain from the API would be genuinely cross-site, and the
// cookie would silently stop being attached: signed in, then anonymous, with
// nothing wrong on either side to find. The fix then is SameSite=None, which
// REQUIRES Secure and therefore HTTPS — so it cannot be set here today without
// breaking local development over http. Set it when a host is chosen, alongside
// the CORS origin list that is already config.
builder.Services
    .AddIdentityApiEndpoints<JobkeepUser>()
    .AddEntityFrameworkStores<IdentityDbContext>();

// ---------------------------------------------------------------------------
// CORS, for the Phase 6 front end
// ---------------------------------------------------------------------------
// The front end's dev server and this API are different origins — :5173 and
// :5080 — so every fetch from a screen is a cross-origin request. Without a
// policy the browser refuses them all, and it refuses them *client*-side, which
// is why this is the kind of gap that gets misdiagnosed as a broken front end.
//
// Three choices here, each of which would be wrong to make differently:
//
//   * A NAMED policy with an explicit origin list, not AllowAnyOrigin. A
//     permissive policy that reaches production is a genuine finding, and
//     "temporary" wildcards are exactly the ones that ship. It is also not a
//     choice that stays available: AllowAnyOrigin and AllowCredentials are
//     mutually exclusive in ASP.NET Core, so writing the wildcard now would
//     have to be undone the moment auth lands and requests start carrying a
//     credential. Better to be in the shape that survives that.
//
//   * Origins from CONFIG, not from code — the same argument the connection
//     string above makes. The dev server's port is a local fact, a deployed
//     front end's origin is a deployment fact, and neither is a code change.
//     appsettings.Development.json holds the default.
//
//   * Registered always, APPLIED only in Development (see UseCors below).
//     AddCors here is inert; nothing happens until middleware runs. A deployed
//     environment therefore has no CORS at all until someone deliberately adds
//     its front-end origin, which is the right default for an API that is about
//     to sit on a public Function URL with no authentication in front of it.
//
// PHASE 11.1b SET AllowCredentials, and this is the paragraph that predicted it.
// Identity's default is a cookie, and a browser will not attach one to a
// cross-origin fetch unless the response says AllowCredentials — so :5173 could
// sign in and then be anonymous on the very next request, with no error anywhere
// on the server. The origin list was already explicit, which is the only reason
// this is one line now: AllowAnyOrigin and AllowCredentials are mutually
// exclusive, so the wildcard this comment refused in Phase 6.1 would have had to
// be unpicked here instead.
//
// PHASE 11.2a RE-READ THE ANTIFORGERY PARAGRAPH, as promised — every controller
// is [Authorize]d now, so a forged cross-site POST can finally do something, and
// "there are no credentials for a browser to attach" has stopped being true.
//
// It stays off, and the thing holding it off is the cookie's SameSite=Lax. Lax
// means the browser attaches the cookie to top-level GET navigations and to
// nothing else — so a cross-site POST, PUT or DELETE from evil.example arrives
// anonymous and gets a 401 before any handler runs. Every state-changing route in
// this app is one of those verbs, the upload included. That is CSRF protection;
// it is just not the antiforgery-token kind.
//
// THE EXPIRY DATE IS THE SAME ONE THE COOKIE ALREADY HAS. The day a deployed
// front end sits on a different site from this API, SameSite has to become None
// to work at all — and None is precisely "attach this cookie to cross-site
// requests", which deletes the paragraph above. Antiforgery tokens become
// mandatory in the same change that sets it, not in a later one, and the two
// notes are deliberately written to be found together.
const string DevCorsPolicy = "localdev";
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173"];

builder.Services.AddCors(options => options.AddPolicy(DevCorsPolicy, policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// ---------------------------------------------------------------------------
// PHASE 13.5 — controllers
// ---------------------------------------------------------------------------
// Every route is an attribute-routed [ApiController] action under Controllers/,
// replacing the five Api/Endpoints/*.cs files 13.1 had to create. Same URLs, same
// responses; what changed is that the composition root has one shape for HTTP.
//
// The two options below are the whole configuration, and both are load-bearing.
//
// SuppressImplicitRequiredAttributeForNonNullableReferenceTypes — this is the
// one that keeps validation in the slice, and it is a smaller lever than it
// looks. By default MVC treats a non-nullable reference type as required, so
// CreateApplicationRequest — a positional record with a non-nullable
// `string Company` — would make POST {} a model-state failure, and
// [ApiController] would answer 400 with its own ProblemDetails before the
// handler ran. GraphQL, meanwhile, would keep answering "Company and Title are
// required." from the slice. That is architecture.md finding A4 — one rule
// enforced differently per surface — coming back through the front door.
//
// Turning the implicit required off puts DOMAIN rules back where every other
// rule lives, in the handler, and Parity/SurfaceParityTests.cs pins that both
// surfaces answer with the same sentence.
//
// What is deliberately NOT turned off is the auto-400 itself
// (SuppressModelStateInvalidFilter), which was the first thing tried. It answers
// for the failures a slice cannot see and must not have to: a body that is
// absent, empty or unparseable, an enum name that does not exist, a multipart
// body the form reader refused mid-stream. With it off, all of those bind null
// and the handler dereferences them — measured, not assumed: an empty body and a
// 6 MB upload both answered 500 with a NullReferenceException. Binding failures
// were never the slice's job; only the rules were.
//
// AddJsonOptions — MVC does NOT read ConfigureHttpJsonOptions below. It has its
// own JsonOptions, and without the converter here an incoming enum sent by name
// ("Interviewing", "JobPosting") would fail to bind. So the split is:
//
//   * REQUESTS deserialize through MVC's copy, configured here.
//   * RESPONSES serialize through the Http.Json copy configured below, because
//     ToHttpResult builds a Results.* value and MVC wraps it rather than
//     re-serializing it. That is what keeps Results.BadRequest("message") a bare
//     JSON string, quotes included, which Rest/ and Parity/ assert on exactly.
//
// One converter, two options objects, because two frameworks are in play. It
// looks like duplication and is not.
builder.Services
    .AddControllers(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    // Serialize/accept enums by name ("Interviewing", "FullTime") instead of by
    // int, so REST payloads are readable and match what GraphQL exposes.
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

    // ReferenceHandler.IgnoreCycles used to be set here, and it was load-bearing:
    // the endpoints returned EF entities whose navigation properties cycle
    // (posting <-> its skills), and without the flag System.Text.Json threw.
    // That was a symptom of leaking the database schema out as the API contract,
    // not a serialization preference (architecture.md A2). Phase 2.3 finished
    // moving every route onto response DTOs, which have no cycles, so the flag
    // came out. If it ever needs to come back, something is returning an entity.
});

// GraphQL (HotChocolate). Runs in-process on the same ASP.NET app, so it rides
// the same Lambda deployment in Phase 10 — no separate service. Resolvers pull
// slice handlers from DI, so GraphQL and REST share one code path.
builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>();

// AddSwaggerGen turns what the ApiExplorer discovers into an OpenAPI document
// Swagger UI can render. AddControllers brings MVC's own explorer, which is why
// 13.5 could drop AddEndpointsApiExplorer() — it left no minimal APIs.
//
// PHASE 11.1b PUT IT BACK, because MapIdentityApi's routes are minimal APIs and
// MVC's explorer does not see them. Without this line the identity endpoints
// exist, work, and are invisible in Swagger UI — which is the one place a human
// is going to try signing in from before the front end has a login screen.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Local-only convenience: apply migrations on startup so `dotnet run` works right
// after the Postgres container comes up. In a deployed environment migrations
// should be applied deliberately (a release step), not automatically on boot.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();

    // PHASE 13.3b — five contexts, five migration histories, and the ORDER
    // matters exactly once. Applications' initial migration creates the three
    // views Analytics reads; the other four are independent of each other,
    // because the split removed every foreign key that crossed a schema. That
    // independence is the deliverable: five migrations that can run in any order
    // are five modules that could be deployed separately.
    //
    // Analytics is absent because it owns nothing to create.
    scope.ServiceProvider.GetRequiredService<ApplicationsDbContext>().Database.Migrate();
    scope.ServiceProvider.GetRequiredService<SkillsDbContext>().Database.Migrate();
    scope.ServiceProvider.GetRequiredService<DocumentsDbContext>().Database.Migrate();
    scope.ServiceProvider.GetRequiredService<AiDbContext>().Database.Migrate();
    scope.ServiceProvider.GetRequiredService<MatchDbContext>().Database.Migrate();

    // PHASE 11.1a. Sixth, and independent of the other five like the four after
    // Applications: nothing in `identity` references another schema yet, and
    // nothing in another schema references it. 11.2 changes that in one
    // direction — OwnerUserId — and the cross-schema rule from 13.3b applies
    // when it does: a foreign key that crosses a schema becomes a contract check
    // in application code, not a constraint.
    scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Database.Migrate();

    // PHASE 14 — the starting skill vocabulary, immediately after the Skills
    // migration that creates the two tables it writes to.
    //
    // Here rather than in an IHostedService, and the reason is the same one that
    // keeps the migrations here: this must finish BEFORE the first request can
    // resolve a skill name, and a hosted service starting concurrently with the
    // server gives no such guarantee. It is two SELECTs and usually no write.
    //
    // Inside the Development block, which is the honest place for it TODAY and
    // will be wrong on the deploy: migrations there are a deliberate release step
    // (see the comment above), and the seed is reference data those migrated
    // tables need. Phase 10 has to run this as part of that step. Noted here
    // rather than pre-solved, because the shape of that step is not decided yet.
    //
    // THE SWITCH IS FOR THE TEST SUITE, and it is not a tuning knob — nothing
    // outside JobkeepAppFactory ever sets it. The suite runs in Development (so
    // that the real migration path is exercised) and Respawn truncates every
    // table between tests, so without this the vocabulary re-materialises after
    // every reset and 228 reference rows turn up inside every unrelated arrange.
    // That breaks Respawn's contract — each test starts from empty — for every
    // future test as well as the three it broke when this landed. The seeder is
    // still covered: SkillVocabularyTests calls it directly, which is also the
    // only way to assert idempotency.
    if (app.Configuration.GetValue("Skills:SeedOnStartup", true))
        await scope.ServiceProvider.GetRequiredService<SkillSeeder>().SeedAsync();
}

// Only expose the interactive docs in Development — no reason to ship
// the UI to a deployed environment (and it keeps the Lambda in Phase 10 lean).
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    // Development-only, and before the Map* calls below — CORS has to run early
    // enough to answer the preflight OPTIONS itself, which never reaches an
    // endpoint. See the AddCors block above for why a deployed environment gets
    // no policy until one is added on purpose.
    app.UseCors(DevCorsPolicy);
}

// PHASE 11.1b. Written out rather than left to WebApplication's auto-insertion,
// because the ORDER against UseCors is the whole point: a preflight OPTIONS
// carries no cookie, so authorization running first would refuse it before the
// CORS middleware ever answered — and that surfaces in the browser as a CORS
// error with nothing on the server to explain it. Calling them here also marks
// them as set, so the framework does not add a second pair further up.
//
// PHASE 11.2a: everything is [Authorize]d now — the five controllers by an
// attribute on each class, GraphQL by RequireAuthorization() on its endpoint.
// These two lines are what turn the cookie into a ClaimsPrincipal, which is what
// 11.2b will read an owner out of.
app.UseAuthentication();
app.UseAuthorization();

// Every REST route in the app, from the five controllers under Controllers/.
// This one line replaced five MapXModule() calls in 13.5, and the routes did not
// move: the controllers carry the same templates the endpoint files did, and
// three of them share the /applications prefix for the reason AiController gives.
//
// ---------------------------------------------------------------------------
// Where the upload's size cap is ACTUALLY enforced
// ---------------------------------------------------------------------------
// The `file.Length > MaxBytes` check inside DocumentsController.Upload is not the
// first line of defence. By the time a [FromForm] parameter is bound, ASP.NET
// Core has already read the whole multipart body — files over 64 KB spool to a
// temp file on disk — so without these two limits a 30 MB upload is written to
// disk in full and only then answered "the limit is 5120KB". The framework
// defaults are 128 MB for a multipart body and 30 MB for the request, both an
// order of magnitude above anything that endpoint wants.
//
// These two do the refusing, before the bytes are stored:
//   - MultipartBodyLengthLimit stops the form reader mid-stream and is what a
//     client sending an oversized part actually hits.
//   - RequestSizeLimit is the belt to that braces: it bounds the whole request,
//     so a body that is oversized in some way the multipart reader would not
//     count still cannot get through.
//
// The handler's own check stays. It is cheap, it produces the friendly message
// with the real numbers in it, and it is the one that fires when a client
// declares a length under the cap — these limits are about what an attacker can
// make the server DO, not about what a user is told.
//
// WHY THIS IS HERE AND NOT AN ATTRIBUTE ON THE ACTION. [RequestSizeLimit] and
// [RequestFormLimits] take compile-time constants, and MaxBytes is configuration
// (DocumentOptions, bound from the "Documents" section). A const would work today,
// because nothing sets that section — and would fail silently the day something
// did: config would raise the app's cap while the transport limit kept refusing
// below it, so the friendly message with the real numbers could never be reached.
// Attaching the same attributes as endpoint metadata, where the bound options
// exist, keeps one number in one place. It is the mechanism the minimal API used
// too (.WithMetadata(new RequestSizeLimitAttribute(...))); MVC builds its filter
// list from endpoint metadata, so both are honoured the same way.
//
// The envelope a multipart body carries on top of the file itself — boundaries,
// part headers, and the three small text fields. 16 KB is far more than that
// costs and far less than a second file would.
const long MultipartEnvelopeSlack = 16 * 1024;
var uploadOptions = app.Services.GetRequiredService<DocumentOptions>();

app.MapControllers().Add(endpoint =>
{
    var action = endpoint.Metadata.OfType<ControllerActionDescriptor>().FirstOrDefault();
    if (action?.ControllerTypeInfo != typeof(DocumentsController) ||
        action.ActionName != nameof(DocumentsController.Upload)) return;

    endpoint.Metadata.Add(new RequestFormLimitsAttribute
    {
        MultipartBodyLengthLimit = uploadOptions.MaxBytes
    });
    endpoint.Metadata.Add(new RequestSizeLimitAttribute(
        uploadOptions.MaxBytes + MultipartEnvelopeSlack));
});

// PHASE 11.1b — the platform's identity endpoints, under /identity.
//
// A GROUP rather than the app root, so they are /identity/register and
// /identity/login rather than sitting beside /applications as if they were this
// app's own vocabulary. It also gives the whole set one Swagger tag.
//
// The COOKIE is the flow this app uses: POST /identity/login?useCookies=true
// sets the application cookie, and the front end sends credentials: 'include'
// from then on (11.1c). The same endpoint without that query string answers with
// a bearer token instead; both schemes are registered because
// AddIdentityApiEndpoints registers both, and the token half is left reachable
// deliberately — it is what a non-browser client would use, and it costs nothing
// to leave working.
var identity = app.MapGroup("/identity").WithTags("Identity");
identity.MapIdentityApi<JobkeepUser>();

// MapIdentityApi has no logout, and that is not an oversight in the framework:
// signing out a bearer token is something only the client can do. With a cookie
// it is the opposite — the cookie is in the browser and only a Set-Cookie from
// here clears it — so this is the one route the group is genuinely missing.
//
// RequireAuthorization is not protection, it is honesty: logging out when you
// were never logged in is a 401, not a silent 204 that implies something happened.
identity.MapPost("/logout", async (SignInManager<JobkeepUser> signIn) =>
{
    await signIn.SignOutAsync();
    return Results.NoContent();
}).RequireAuthorization();

// Serves POST /graphql for queries + the Nitro (Banana Cake Pop) IDE at GET /graphql.
//
// PHASE 11.2a — RequireAuthorization() on the endpoint, NOT [Authorize] on the
// resolvers. The attribute version needs HotChocolate.AspNetCore.Authorization, a
// new package, to buy per-field policies this app has no use for: there is one
// rule here and it is "be signed in". ASP.NET's own endpoint authorization is
// already registered, applies to the whole surface at once, and cannot be
// forgotten on a new resolver the way a per-field attribute can.
//
// It covers the Nitro IDE at GET /graphql too, which is the one visible cost — a
// signed-out browser gets a 401 instead of the IDE. Signing in on :5173 sets the
// cookie for localhost, and the IDE then loads, so this is not the dead end it
// looks like. Development-only either way.
//
// F5 is the reason this line is not an afterthought: the last surface-specific
// exposure in this app was GraphQL-only, so "REST is locked" is not an answer.
// AuthorizationTests asks both surfaces the same question over the wire.
app.MapGraphQL().RequireAuthorization();

app.Run();

// Top-level statements compile to an *internal* Program class, which
// WebApplicationFactory<Program> in tests/Jobkeep.Tests cannot name. This marker
// makes that generated class public without changing any behaviour. Preferred over
// InternalsVisibleTo because it exposes exactly one type instead of every internal.
public partial class Program { }
