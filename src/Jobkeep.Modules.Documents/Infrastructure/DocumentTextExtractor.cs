using System.Text;
using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Jobkeep.Modules.Documents.Domain;
using Jobkeep.SharedKernel;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Jobkeep.Modules.Documents;

// The deterministic half of the pipeline: bytes in, plain text out.
//
// ---------------------------------------------------------------------------
// Why this is not the model's job
// ---------------------------------------------------------------------------
// It would be possible to hand a whole PDF to a multimodal model and ask for
// structured output in one step, and it is worth saying why that is the wrong
// shape rather than just not doing it.
//
// Extraction has a right answer. A PDF contains the exact glyphs and their exact
// positions; getting the text out is a library call that returns the same string
// every time and fails loudly when it cannot. Folding it into the model step
// would convert a deterministic operation with a checkable result into a sampled
// one — and it would make every extraction bug indistinguishable from a
// structuring bug, because there would no longer be an intermediate value to
// look at.
//
// This is also what commercial resume parsers do. Textkernel, Affinda and DaXtra
// all separate document conversion from field extraction; the second stage has
// moved from statistical sequence models to LLMs over the last few years, but
// the seam between the two has not moved. It is a seam worth having.
//
// ---------------------------------------------------------------------------
// Content sniffing, not extension trust
// ---------------------------------------------------------------------------
// The format is decided by the leading bytes. The filename and the client's
// content-type are both attacker-controlled and, much more often, simply wrong —
// people rename files. `resume.pdf` that is really a DOCX should import, and
// `resume.txt` that is really a 4 MB PDF should not be read as text.
public interface IDocumentTextExtractor
{
    SliceResult<ExtractedDocument> Extract(byte[] bytes, string fileName);
}

// The result of the deterministic half. Warning carries something imperfect that
// did not stop the import — currently only the scanned-PDF case.
public record ExtractedDocument(SourceFormat Format, string Text, string? Warning);

public class DocumentTextExtractor : IDocumentTextExtractor
{
    private readonly DocumentOptions _options;

    public DocumentTextExtractor(DocumentOptions options) => _options = options;

    // File signatures. Only the ones that decide a branch below are listed;
    // this is not a general-purpose magic-number table and should not become one.
    private static readonly byte[] Pdf = "%PDF"u8.ToArray();
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];              // "PK\x03\x04"
    private static readonly byte[] Ole2 = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] Rtf = "{\\rtf"u8.ToArray();

    public SliceResult<ExtractedDocument> Extract(byte[] bytes, string fileName)
    {
        // The size cap is checked here, before anything parses, because that is
        // the only place it means anything. A cap applied after the parse is a
        // cap on nothing — the memory has already been spent.
        if (bytes.Length == 0)
            return SliceResult<ExtractedDocument>.Invalid("The uploaded file is empty.");

        if (bytes.Length > _options.MaxBytes)
            return SliceResult<ExtractedDocument>.Invalid(
                $"That file is {bytes.Length / 1024}KB. The limit is {_options.MaxBytes / 1024}KB.");

        // The legacy binary .doc check comes first and is deliberately explicit.
        //
        // This is the scope trap in this phase: in a switch statement `.doc`
        // looks like one more case, and it is not. The pre-2007 format is an OLE2
        // compound file with no good free pure-managed extractor — the options
        // are a native dependency or a commercial licence, and neither belongs in
        // a portfolio repo that has to build on a clean machine. A clear message
        // telling the user to re-save costs nothing and is the honest answer.
        //
        // OLE2 also covers .xls and .ppt, so the message names the case we mean
        // rather than claiming to know which Office format it was.
        if (StartsWith(bytes, Ole2))
            return SliceResult<ExtractedDocument>.Invalid(
                "That looks like a pre-2007 Office file (.doc). Open it and 'Save As' .docx, "
                + "or export it as PDF, then upload that.");

        if (StartsWith(bytes, Rtf))
            return SliceResult<ExtractedDocument>.Invalid(
                "RTF is not supported. Save the document as .docx or PDF and upload that.");

        if (StartsWith(bytes, Pdf))
            return ExtractPdf(bytes);

        if (StartsWith(bytes, Zip))
            return ExtractDocx(bytes);

        return ExtractPlainText(bytes, fileName);
    }

    private SliceResult<ExtractedDocument> ExtractPdf(byte[] bytes)
    {
        string text;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var pdf = PdfDocument.Open(stream);

            // -----------------------------------------------------------------
            // Reading order is reconstructed from geometry, not taken on trust
            // -----------------------------------------------------------------
            // A PDF stores glyphs with coordinates, not a document. Nothing in
            // the format records that a page has two columns, so "the order of
            // the text" is a question every extractor has to answer for itself,
            // and the cheap answers are wrong on exactly the documents this
            // feature exists for.
            //
            // This used to call ContentOrderTextExtractor, whose comment claimed
            // it was "right far more often and never worse". A real two-column
            // resume disproved the second half: it orders by the content stream,
            // so a sidebar layout came out as every date, then every section
            // heading, then every employer - each cluster torn from the entries
            // it belonged to, and two columns of one line concatenated without
            // even a space ("...Platform courseSoftware Architecture"). The model
            // downstream then had to reassemble a document from confetti. It did
            // better than expected and still lost every real skill, because the
            // skills were in bullets and the things sitting where skills belong
            // were course titles from the other column.
            //
            // The three-stage pipeline below is PdfPig's document-layout
            // analysis, already in the referenced package - no new dependency:
            //
            //   1. NearestNeighbourWordExtractor  glyphs -> words, by spacing
            //   2. DocstrumBoundingBoxes          words  -> blocks, by density
            //   3. UnsupervisedReadingOrderDetector  blocks -> reading order
            //
            // Step 2 is what understands columns: Docstrum measures the spacing
            // between nearest-neighbour words and groups what is genuinely
            // adjacent, so a sidebar becomes its own blocks rather than being
            // interleaved with the body. Step 3 then orders those blocks the way
            // a person reads them.
            //
            // The cost, stated: this is a heuristic over geometry and it can
            // mis-segment an unusual layout, where the old path was merely
            // deterministic about being wrong. That trade is worth taking because
            // a wrong ORDER is recoverable by the model and by the human review
            // screen, while the old failure silently destroyed which facts
            // belonged together.
            var builder = new StringBuilder();
            foreach (var page in pdf.GetPages())
            {
                var words = page.GetWords(NearestNeighbourWordExtractor.Instance);
                var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);

                if (blocks.Count == 0)
                {
                    // Segmentation found nothing to group - a page of pure
                    // graphics, or a layout Docstrum could not read. Fall back
                    // rather than silently dropping the page; the old extractor
                    // is still better than nothing, and this is the only path on
                    // which it runs.
                    builder.AppendLine(ContentOrderTextExtractor.GetText(page));
                    builder.AppendLine();
                    continue;
                }

                // A blank line between blocks, which the old path could not
                // provide at all. Blocks are semantic groups, so this hands the
                // model paragraph boundaries instead of one undifferentiated
                // wall - the same structure Normalise() preserves deliberately.
                foreach (var block in UnsupervisedReadingOrderDetector.Instance.Get(blocks))
                {
                    builder.AppendLine(block.Text);
                    builder.AppendLine();
                }
            }
            text = builder.ToString();
        }
        catch (Exception ex)
        {
            // A malformed PDF is the caller's problem, not a server fault. The
            // parser is managed code over a hostile-input format, so it is
            // treated as something that will throw eventually and answers 400
            // rather than an unhandled 500.
            return SliceResult<ExtractedDocument>.Invalid(
                $"That PDF could not be read: {ex.Message}");
        }

        var cleaned = Normalise(text);

        // A scanned PDF is a picture of a document. It opens fine, has pages, and
        // yields no text — and the failure this guards against is silent: an
        // empty resume stored successfully, and a Phase 5 match check that
        // cheerfully reports you match none of the keywords. Say it out loud
        // instead. OCR would fix it properly and is a different project.
        if (cleaned.Length < _options.MinTextChars)
            return SliceResult<ExtractedDocument>.Ok(new ExtractedDocument(
                SourceFormat.Pdf,
                cleaned,
                "This PDF has almost no selectable text, which usually means it is a scan or "
                + "an image export. Nothing can be parsed out of it. Upload a text-based PDF "
                + "or a .docx instead."));

        return SliceResult<ExtractedDocument>.Ok(
            new ExtractedDocument(SourceFormat.Pdf, cleaned, null));
    }

    private SliceResult<ExtractedDocument> ExtractDocx(byte[] bytes)
    {
        // ---------------------------------------------------------------------
        // Zip bomb guard, before OpenXml is handed the bytes
        // ---------------------------------------------------------------------
        // A .docx is a zip, so MaxBytes bounds the upload but not the work. This
        // reads only the central directory - it decompresses nothing - and sums
        // what the archive CLAIMS each entry unzips to. A bomb that declares its
        // real size dies here, cheaply, before OpenXml opens a single part.
        //
        // A crafted archive can of course understate those numbers, which is why
        // this is not the only check: the accumulation loop below bounds the text
        // that actually comes out. Two cheap bounds beat one clever one.
        try
        {
            using var probe = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(probe, ZipArchiveMode.Read);

            long declared = 0;
            foreach (var entry in archive.Entries)
            {
                declared += entry.Length;
                if (declared > _options.MaxDecompressedBytes)
                    return SliceResult<ExtractedDocument>.Invalid(
                        "That .docx unpacks to far more than a document should. "
                        + "It has been refused rather than opened.");
            }
        }
        catch (InvalidDataException)
        {
            return SliceResult<ExtractedDocument>.Invalid(
                "That file is a zip archive but its contents could not be read.");
        }

        string text;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var doc = WordprocessingDocument.Open(stream, isEditable: false);

            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null)
                return SliceResult<ExtractedDocument>.Invalid(
                    "That .docx has no document body — it may be corrupt.");

            // Descendants<Paragraph>() rather than walking Body's direct children,
            // and this matters more for resumes than for anything else: resume
            // templates overwhelmingly lay out contact blocks and skill lists in
            // tables, and a paragraph inside a table cell is not a child of the
            // body. Walking descendants picks them up in document order.
            var builder = new StringBuilder();
            foreach (var paragraph in body.Descendants<Paragraph>())
            {
                builder.AppendLine(paragraph.InnerText);

                // The second bound, over what actually came out rather than what
                // the archive declared. Rejecting rather than truncating: half a
                // resume imported silently is worse than an honest refusal, and
                // nothing legitimate reaches this size.
                if (builder.Length > _options.MaxDecompressedBytes)
                    return SliceResult<ExtractedDocument>.Invalid(
                        "That .docx contains far more text than a document should. "
                        + "It has been refused rather than parsed.");
            }

            text = builder.ToString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Includes the case where the zip is a real zip but not a Word
            // document — OpenXml throws rather than returning null for that.
            return SliceResult<ExtractedDocument>.Invalid(
                $"That file is a zip archive but not a readable .docx: {ex.Message}");
        }

        var cleaned = Normalise(text);
        if (cleaned.Length < _options.MinTextChars)
            return SliceResult<ExtractedDocument>.Invalid(
                "That .docx contains no text.");

        return SliceResult<ExtractedDocument>.Ok(
            new ExtractedDocument(SourceFormat.Docx, cleaned, null));
    }

    private SliceResult<ExtractedDocument> ExtractPlainText(byte[] bytes, string fileName)
    {
        // A NUL byte inside the first block is the cheap, reliable "this is
        // binary" test, and it is what stops an unrecognised binary format from
        // being stored as mojibake and sent to a model as if it were a resume.
        var probe = bytes.AsSpan(0, Math.Min(bytes.Length, 8192));
        if (probe.IndexOf((byte)0) >= 0)
            return SliceResult<ExtractedDocument>.Invalid(
                "That file is not a document this app can read. Supported: PDF, .docx, .txt and .md.");

        string text;
        try
        {
            // Throw-on-invalid rather than the default replacement behaviour: a
            // file that is not valid UTF-8 should be rejected, not silently
            // stored full of U+FFFD. The BOM, if present, is stripped by
            // GetString's encoding preamble handling below.
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return SliceResult<ExtractedDocument>.Invalid(
                "That file is not valid UTF-8 text. Save it as UTF-8, or upload a PDF or .docx.");
        }

        var cleaned = Normalise(text);
        if (cleaned.Length < _options.MinTextChars)
            return SliceResult<ExtractedDocument>.Invalid("That file contains no text.");

        // The only place the extension is consulted at all, and only to pick
        // between two formats whose handling is identical. Getting it wrong costs
        // nothing.
        var format = fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? SourceFormat.Markdown
            : SourceFormat.PlainText;

        return SliceResult<ExtractedDocument>.Ok(new ExtractedDocument(format, cleaned, null));
    }

    private static bool StartsWith(byte[] bytes, byte[] signature) =>
        bytes.Length >= signature.Length && bytes.AsSpan(0, signature.Length).SequenceEqual(signature);

    // Collapses the whitespace damage that PDF and DOCX extraction both produce:
    // CRLF variants to \n, runs of blank lines to one, and trailing spaces gone.
    //
    // Worth doing before the text is stored rather than before it is sent to the
    // model, because the stored text is what a human reads when diagnosing a bad
    // parse — and a resume rendered as 300 blank-separated fragments is unreadable
    // for exactly the case where reading it matters.
    private static string Normalise(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var builder = new StringBuilder(text.Length);
        var blankRun = 0;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
            {
                // One blank line is a paragraph break and carries structure the
                // model uses; four in a row is a PDF artefact.
                if (++blankRun > 1) continue;
                builder.Append('\n');
                continue;
            }

            blankRun = 0;
            builder.Append(trimmed).Append('\n');
        }

        return builder.ToString().Trim();
    }
}
