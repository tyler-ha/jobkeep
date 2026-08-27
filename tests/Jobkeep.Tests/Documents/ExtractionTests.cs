using Jobkeep.Models;
using Jobkeep.Modules.Documents;
using Jobkeep.Shared;

namespace Jobkeep.Tests.Documents;

/// <summary>
/// Phase 4.5 — the deterministic half of the import pipeline, against real files.
///
/// <para>
/// These are plain unit tests with no database and no HTTP, which this project
/// normally refuses: the standing rule is that an integration test through the
/// real surface beats a unit test, because the bugs this codebase actually has —
/// SQL that does not translate, delete behaviour, one rule enforced on one
/// surface only — are invisible without the real thing.
/// </para>
///
/// <para>
/// The rule is not being bent. It says to test the real thing, and here the real
/// thing is a byte array and a parser. <see cref="DocumentTextExtractor"/> touches
/// no database, no HTTP context and no model; a container would add ninety seconds
/// and prove nothing. The two sanctioned exceptions already in the suite are the
/// same shape — <c>Domain/</c> for a pure function of two enums, and the Phase 4
/// model fake for a dependency whose real behaviour is non-deterministic.
/// </para>
///
/// <para>
/// What makes these worth having is that the inputs are REAL FILES, not strings
/// pretending to be files. A hand-written PDF still has to satisfy PdfPig's xref
/// parser, and the .docx still has to satisfy OpenXml's package reader. See
/// Fixtures/README.md for how they were built and why they are checked in.
/// </para>
/// </summary>
public class ExtractionTests
{
    private static readonly DocumentOptions Options = new();
    private static readonly DocumentTextExtractor Extractor = new(Options);

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static ExtractedDocument Extract(string name)
    {
        var result = Extractor.Extract(Fixture(name), name);
        Assert.Equal(ResultStatus.Ok, result.Status);
        return result.Value!;
    }

    [Fact]
    public void Pdf_ExtractsTheTextAndReportsTheFormat()
    {
        var extracted = Extract("resume.pdf");

        Assert.Equal(SourceFormat.Pdf, extracted.Format);
        Assert.Null(extracted.Warning);
        Assert.Contains("Jane Doe", extracted.Text);
        Assert.Contains("jane.doe@example.com", extracted.Text);
        // The education section is at the BOTTOM of the document. Asserting on it
        // is what catches an extractor that reads only the first page or stops
        // early — which is the failure mode that would silently cost this feature
        // exactly the records it exists to produce.
        Assert.Contains("University of Melbourne", extracted.Text);
    }

    [Fact]
    public void Docx_ExtractsTextFromInsideTablesToo()
    {
        var extracted = Extract("resume.docx");

        Assert.Equal(SourceFormat.Docx, extracted.Format);
        Assert.Contains("Jane Doe", extracted.Text);

        // The fixture puts these last two lines inside a table cell, because
        // resume templates do. A paragraph in a cell is not a child of the body,
        // so an extractor walking Body's direct children returns the document
        // MINUS its skills section — silently, and only for the documents most
        // likely to be real resumes.
        Assert.Contains("SKILLS", extracted.Text);
        Assert.Contains("Kubernetes", extracted.Text);
    }

    [Fact]
    public void PlainText_IsReadAsUtf8()
    {
        var extracted = Extract("resume.txt");

        Assert.Equal(SourceFormat.PlainText, extracted.Format);
        Assert.Contains("Canva", extracted.Text);
    }

    [Fact]
    public void Markdown_IsDistinguishedFromPlainTextByExtensionOnly()
    {
        // The only place the extension is consulted at all, and only to choose
        // between two formats whose handling is identical.
        var result = Extractor.Extract(Fixture("resume.txt"), "resume.md");

        Assert.Equal(ResultStatus.Ok, result.Status);
        Assert.Equal(SourceFormat.Markdown, result.Value!.Format);
    }

    [Fact]
    public void FormatIsSniffedFromContent_NotFromTheFileName()
    {
        // A PDF named .txt is still a PDF. The filename and the client's
        // content-type are both wrong often enough (people rename files) and
        // controllable enough that neither can decide this.
        var result = Extractor.Extract(Fixture("resume.pdf"), "resume.txt");

        Assert.Equal(ResultStatus.Ok, result.Status);
        Assert.Equal(SourceFormat.Pdf, result.Value!.Format);
    }

    [Fact]
    public void ScannedPdf_ExtractsWithAWarningRatherThanFailingSilently()
    {
        // A PDF with no text layer opens perfectly and yields nothing. The whole
        // risk is that this reads as success: an empty resume stored, and a
        // Phase 5 ATS check reporting that you match no keywords at all.
        var result = Extractor.Extract(Fixture("scanned.pdf"), "scanned.pdf");

        Assert.Equal(ResultStatus.Ok, result.Status);
        Assert.NotNull(result.Value!.Warning);
        Assert.Contains("scan", result.Value.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyDoc_IsRefusedWithAnActionableMessage()
    {
        // The scope trap of this phase. There is no good free pure-managed
        // extractor for the pre-2007 binary format, so the honest answer is a
        // message telling the user what to do instead of a half-working parse.
        var result = Extractor.Extract(Fixture("legacy.doc"), "resume.doc");

        Assert.Equal(ResultStatus.Invalid, result.Status);
        Assert.Contains(".docx", result.Error);
    }

    [Fact]
    public void Rtf_IsRefused()
    {
        var result = Extractor.Extract("{\\rtf1\\ansi hello}"u8.ToArray(), "resume.rtf");

        Assert.Equal(ResultStatus.Invalid, result.Status);
        Assert.Contains("RTF", result.Error);
    }

    [Fact]
    public void BinaryRubbish_IsRefusedRatherThanStoredAsMojibake()
    {
        // A NUL byte is the cheap, reliable "this is not text" test. Without it
        // an unrecognised binary format would be stored as replacement characters
        // and sent to a model as if it were a resume.
        var result = Extractor.Extract([0x00, 0x01, 0x02, 0x03, 0x04, 0x05], "mystery.bin");

        Assert.Equal(ResultStatus.Invalid, result.Status);
    }

    [Fact]
    public void InvalidUtf8_IsRefusedRatherThanReplaced()
    {
        // 0xC3 starts a two-byte sequence that never arrives. The default
        // decoder would substitute U+FFFD and carry on; throwOnInvalidBytes is
        // what makes this a refusal instead of silently corrupted text.
        var result = Extractor.Extract([0x48, 0x69, 0xC3, 0x28, 0x21], "resume.txt");

        Assert.Equal(ResultStatus.Invalid, result.Status);
        Assert.Contains("UTF-8", result.Error);
    }

    [Fact]
    public void OversizedUpload_IsRefusedBeforeAnythingParses()
    {
        var options = new DocumentOptions { MaxBytes = 128 };
        var extractor = new DocumentTextExtractor(options);

        var result = extractor.Extract(Fixture("resume.pdf"), "resume.pdf");

        Assert.Equal(ResultStatus.Invalid, result.Status);
        Assert.Contains("limit", result.Error);
    }

    [Fact]
    public void EmptyUpload_IsRefused()
    {
        var result = Extractor.Extract([], "empty.txt");
        Assert.Equal(ResultStatus.Invalid, result.Status);
    }

    [Fact]
    public void Normalisation_CollapsesBlankRunsButKeepsParagraphBreaks()
    {
        // One blank line is structure the model uses to tell sections apart; four
        // in a row is a PDF artefact. Both extractors produce plenty of the latter.
        var messy = "Jane Doe   \r\n\r\n\r\n\r\nEXPERIENCE\r\n\r\nCanva   \r\n"u8.ToArray();

        // MinTextChars lowered for this one case. The sample is deliberately tiny
        // so the expected output can be written out in full and compared exactly,
        // and at 26 characters the real 40-character floor would reject it as
        // "no text" before normalisation was ever reached. Lowering the threshold
        // isolates the behaviour under test; the floor itself is covered by
        // ScannedPdf_ExtractsWithAWarningRatherThanFailingSilently.
        var extractor = new DocumentTextExtractor(new DocumentOptions { MinTextChars = 1 });

        var result = extractor.Extract(messy, "resume.txt");

        Assert.Equal(ResultStatus.Ok, result.Status);
        Assert.Equal("Jane Doe\n\nEXPERIENCE\n\nCanva", result.Value!.Text);
    }
}
