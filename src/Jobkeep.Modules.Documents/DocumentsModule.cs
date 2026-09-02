using Jobkeep.Models;
using Jobkeep.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jobkeep.Modules.Documents;

// Configuration for document import. A plain class registered as a singleton
// rather than IOptions<T>, matching AiOptions: nothing here reloads, and
// IOptions buys change-notification this project has no use for while costing an
// unwrap in every handler.
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

        services.AddScoped<ImportDocumentHandler>();
        services.AddScoped<GetImportHandler>();
        services.AddScoped<ListImportsHandler>();
        services.AddScoped<ReviewImportHandler>();
        services.AddScoped<RestructureImportHandler>();
        services.AddScoped<CommitImportHandler>();
        services.AddScoped<DiscardImportHandler>();
        services.AddScoped<AddSkillToResumeHandler>();
        services.AddScoped<ListResumesHandler>();
        services.AddScoped<GetResumeHandler>();
        services.AddScoped<RemoveSkillFromResumeHandler>();
        // PHASE 13.3c. The endpoint DiscardImport's error message has been
        // pointing at since Phase 4.5; DeleteResume.cs says why it is only
        // now real, and what its two refusals cost.
        services.AddScoped<DeleteResumeHandler>();

        return services;
    }
}
