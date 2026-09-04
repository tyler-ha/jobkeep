using System.Text;
using Jobkeep.Modules.Documents.Domain;
using Jobkeep.SharedKernel;
using Mediator;

namespace Jobkeep.Modules.Documents;

// Slice: paste an advertisement instead of uploading a file. Phase 6.5 group 4.
//
// ---------------------------------------------------------------------------
// Why this is a sibling route and not `file` becoming optional
// ---------------------------------------------------------------------------
// One endpoint with two mutually exclusive bodies - multipart-with-a-file OR
// JSON-with-a-string - is a shape OpenAPI represents badly, and getting there
// means putting more binding attributes next to an IFormFile. That is the exact
// neighbourhood of the Phase 4.5 trap where one unrepresentable route made
// GET /swagger/v1/swagger.json answer 500 for the whole document. Two routes
// cost a file; one route with two bodies costs a Swagger outage.
//
// ---------------------------------------------------------------------------
// Why this delegates rather than doing the work
// ---------------------------------------------------------------------------
// A paste and an uploaded .txt are the SAME DOCUMENT, so they must not be two
// code paths that agree by inspection. Everything after "here are the bytes" -
// the NUL probe, the strict-UTF8 guard, Normalise, the label resolution, the
// content hash, the save-before-parse ordering, the enqueue - is
// ImportDocumentHandler's, unchanged. This slice owns exactly one rule that the
// file path cannot have (a paste too short to be an ad is a mistake, where a
// scanned PDF with no text layer is a real document) and then hands over.
//
// That rule lives HERE rather than in the controller because REST and GraphQL
// both reach it, and a rule at the edge is a rule each surface gets to enforce
// differently.
public record ImportText(
    string Text,
    DocumentKind Kind,
    string? Label,
    string? SourceUrl,
    string? Name) : IRequest<SliceResult<ImportResponse>>;

public class ImportTextHandler : IRequestHandler<ImportText, SliceResult<ImportResponse>>
{
    private readonly ISender _sender;
    private readonly DocumentOptions _options;

    public ImportTextHandler(ISender sender, DocumentOptions options)
    {
        _sender = sender;
        _options = options;
    }

    public async ValueTask<SliceResult<ImportResponse>> Handle(ImportText message, CancellationToken ct)
    {
        // Trimmed once, and the TRIMMED bytes are what gets hashed and stored.
        // Selecting an ad in a browser picks up leading and trailing whitespace
        // that is an artefact of the selection, not of the document - so two
        // pastes of the same ad that differ only in how far the drag went are
        // the same import, and a paste matches a .txt of the same words.
        var text = message.Text?.Trim() ?? "";

        // The one rule the file path deliberately does not share. Under the same
        // threshold a FILE is saved anyway and warned about (a scan with no text
        // layer is a real document someone can still act on); a PASTE is refused,
        // because a twelve-character paste is a slip and saving it would leave a
        // row whose only possible use is to be discarded.
        if (text.Length < _options.MinTextChars)
            return SliceResult<ImportResponse>.Invalid(
                $"That is only {text.Length} characters. Paste the whole advertisement - "
                + $"at least {_options.MinTextChars}.");

        // No extension on the default name, so the extractor calls it PlainText
        // rather than Markdown. A name the caller supplied is passed through and
        // truncated by ImportDocumentHandler along with every other filename.
        return await _sender.Send(
            new ImportDocument(
                Encoding.UTF8.GetBytes(text),
                string.IsNullOrWhiteSpace(message.Name) ? "Pasted text" : message.Name.Trim(),
                message.Kind,
                message.Label,
                message.SourceUrl),
            ct);
    }
}
