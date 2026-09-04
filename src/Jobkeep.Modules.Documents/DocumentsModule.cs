using Jobkeep.Contracts.Applications;
using Jobkeep.Contracts.Documents;
using Jobkeep.Contracts.Skills;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Modules.Documents;

// Configuration for document import, bound from the "Documents" section of
// appsettings. A plain class registered as a singleton rather than IOptions<T>,
// matching AiOptions: nothing here reloads, and IOptions buys change-notification
// this project has no use for while costing an unwrap in every handler.
//
// The defaults below and the values in appsettings.json are EQUAL, and are meant
// to stay equal — the JSON exists so the limits are reviewable without reading
// C#, not so they can drift. Note the direction that creates: appsettings now
// WINS, so a typo there silently changes a limit that used to be unchangeable.
// The reasoning for each number stays here, beside the property; the JSON carries
// the value and one line on what it protects.
//
// ParseInBackground is the exception and is deliberately NOT in appsettings —
// see its own comment below.
public class DocumentOptions
{
    // The hard size cap, checked before anything parses. 5 MB is generous for a
    // resume — a text-based PDF resume is usually under 200 KB — and it is small
    // enough that a malicious upload cannot make the process do meaningful work.
    //
    // This is the app's first real attack surface, and the cap is the cheapest of
    // the mitigations: everything downstream (a PDF parser, a zip reader, a model
    // with a context window) has cost proportional to input size, so bounding the
    // input bounds all of them at once.
    public long MaxBytes { get; set; } = 5 * 1024 * 1024;

    // Below this many characters, a document is treated as having no text. It
    // catches the scanned-PDF case, where the file parses perfectly and yields
    // nothing — see DocumentTextExtractor.
    public int MinTextChars { get; set; } = 40;

    // How much text the model is shown. Set high enough that it effectively never
    // fires on a real resume: a dense three-page resume is around 8,000
    // characters. DocumentStructurer explains why head-truncation is a worse
    // answer for a resume than it was for a job ad in Phase 4, and why the fix is
    // a bigger limit plus an honest warning rather than chunking.
    public int MaxStructureChars { get; set; } = 24000;

    // The ceiling on what a .docx is allowed to expand to once unzipped, and the
    // only limit here that exists purely for an attacker rather than for a user.
    //
    // MaxBytes bounds what arrives; it does not bound what that turns into. A
    // .docx is a zip, and zip is a compressing format: a few hundred KB of
    // deliberately crafted archive decompresses to gigabytes, which is a
    // memory-exhaustion DoS that costs the attacker one small upload. The
    // extractor therefore checks the archive's declared uncompressed total, and
    // then checks the text it actually accumulates, because a crafted archive
    // can lie about the first number.
    //
    // 64 MB is roughly thirteen times the input cap - far above any real resume
    // (a text-heavy .docx unzips to perhaps 10x, and a resume is tens of KB
    // either way) and far below the ratios a bomb needs to do damage.
    public long MaxDecompressedBytes { get; set; } = 64 * 1024 * 1024;

    // The review queue is not paged — it is a list of things you have not
    // finished. This caps it so an unbounded query can never be issued.
    public int MaxListSize { get; set; } = 200;

    // Whether ImportParseWorker runs. Phase 6.5 group 6.
    //
    // THIS IS A TEST SEAM, NOT A TUNING KNOB — nothing outside JobkeepAppFactory
    // ever sets it, and it is the same seam and the same reasoning as
    // Skills:SeedOnStartup. The suite truncates every table between tests
    // (Respawn), so a worker running alongside would parse rows out from under
    // unrelated arranges and make the whole suite depend on timing. The tests
    // drive /reparse explicitly instead, which is what the worker does anyway.
    //
    // Turned back ON by the one test that covers the worker itself, because a
    // background mechanism nobody exercises is a background mechanism that has
    // never run.
    public bool ParseInBackground { get; set; } = true;
}

// Module wiring for Documents: DI plus the /imports routes.
//
// ---------------------------------------------------------------------------
// What this module owns
// ---------------------------------------------------------------------------
// `document_imports`, `resumes`, `resume_skills`, `resume_experiences` and
// `resume_educations`. That is the whole list, and since Phase 13.2c it is also
// the whole list of tables this module's code can NAME: every handler takes
// DocumentsDbContext, which exposes those five DbSets and nothing else.
//
// It reaches two other modules, both through Jobkeep.Contracts and neither
// through a project reference:
//
//   * ISkillCatalog for the shared `skills` taxonomy, which Documents used to
//     write directly on the grounds that the table belonged to no module. A
//     table nobody owns is a table nobody can extract, so Skills owns it now.
//   * IApplicationContract to turn a confirmed job-ad draft into a logged
//     application. That used to be a direct call into Applications' handlers
//     across a project reference (architecture.md decision 15); the rules still
//     run in Applications, and only the caller changed.
//
// It calls a language model and does not live in the Ai module, which is the
// point Shared/ModelClient.cs makes at length: Ai owns a table, not a technology.
public static class DocumentsModule
{
    public static IServiceCollection AddDocumentsModule(
        this IServiceCollection services, IConfiguration config)
    {
        var options = new DocumentOptions();
        config.GetSection("Documents").Bind(options);
        services.AddSingleton(options);

        // Both are stateless and hold no scoped dependency of their own, so
        // singleton is safe — but they are registered scoped anyway to match the
        // handlers that consume them, because DocumentStructurer's constructor
        // takes IChatClient and a future decorator around it (retry, logging,
        // caching) would very plausibly be scoped. Registering them scoped now
        // costs nothing and removes a captive-dependency trap later.
        services.AddScoped<IDocumentTextExtractor, DocumentTextExtractor>();
        services.AddScoped<IDocumentStructurer, DocumentStructurer>();

        // Phase 13.2d. The contract Applications uses to check a résumé id and
        // render its label. Registered by the owning module rather than by its
        // caller, for the reason ApplicationsModule gives about IPostingContract:
        // a contract registered by whichever consumer happens to be wired first
        // is a dependency nothing in Program.cs shows.
        services.AddScoped<IResumeContract, ResumeContract>();

        // PHASE 6.5 GROUP 6 — the import parse queue and its worker.
        //
        // The queue is registered unconditionally because ImportDocumentHandler
        // injects it and enqueues into it whether or not anything is reading:
        // the durable queue is the Parsing status, so an unread channel loses
        // nothing that the startup sweep would not have found anyway. Only the
        // WORKER is behind the flag, which keeps the seam to one moving part.
        services.AddSingleton<ImportParseQueue>();
        if (options.ParseInBackground)
            services.AddHostedService<ImportParseWorker>();

        // PHASE 13.4 — the AddScoped<XHandler>() lines that were here are gone.
        // AddMediator() in Program.cs registers every IRequestHandler<,> and
        // INotificationHandler<> it finds in the referenced module assemblies, so
        // a new slice is now a file and a route, with nothing to remember to
        // register. What stays below is what a mediator cannot know about.

        return services;
    }
}
