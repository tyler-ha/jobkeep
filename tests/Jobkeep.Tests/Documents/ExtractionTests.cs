using System.IO.Compression;
using Jobkeep.Modules.Documents;
using Jobkeep.Modules.Documents.Domain;
using Jobkeep.SharedKernel;

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
        // Phase 5 match check reporting that you match no keywords at all.
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

    // ------------------------------------------------- two columns (Phase 4.5b)

    [Fact]
    public void TwoColumnPdf_KeepsEachColumnTogether_RatherThanInterleavingThem()
    {
        // The defect a real exported CV found, reduced to one fixture.
        //
        // A PDF stores glyphs with coordinates, not a document. Nothing in the
        // format records that a page has two columns, so an extractor that
        // trusts the order the content stream happens to draw in emits a sidebar
        // and a body zipped together line by line. two-column.pdf writes its
        // operators in exactly that interleaved order, on purpose, so the old
        // behaviour is reproducible rather than anecdotal:
        //
        //     CONTACT           EXPERIENCE          <- one line, both columns
        //     jane@example.test Northwind Traders
        //
        // Which is not a cosmetic problem. It destroys WHICH FACTS BELONG
        // TOGETHER, and everything downstream inherits that: on the real resume
        // this was found with, every genuine skill was missed because the skills
        // lived in body bullets while the text sitting where skills belonged had
        // come from the other column.
        var extracted = Extract("two-column.pdf");

        // The failing assertion, stated as the thing a reader can picture: no
        // single line may carry text from both columns.
        Assert.DoesNotContain("CONTACT EXPERIENCE", extracted.Text);
        Assert.DoesNotContain("jane@example.test Northwind", extracted.Text);

        // And the positive half - each column arrives contiguous, so a consumer
        // can tell an employer from the line under it.
        Assert.Contains("Northwind Traders\nSenior Engineer", extracted.Text);
        Assert.Contains("jane@example.test\n0400 000 000", extracted.Text);

        // Nothing was dropped on the way. A reading-order fix that loses text is
        // a worse bug than the one it fixes, and a segmenter that mis-groups can
        // do exactly that.
        foreach (var line in new[]
                 {
                     "CONTACT", "jane@example.test", "0400 000 000", "Melbourne",
                     "SKILLS", "PostgreSQL", "EXPERIENCE", "Northwind Traders",
                     "Senior Engineer", "Cut median settlement latency by forty percent.",
                     "Contoso Freight", "Engineer"
                 })
            Assert.Contains(line, extracted.Text);
    }

    // ------------------------------------------------------- zip bomb (review)

    /// <summary>
    /// Builds a real, valid zip whose single entry decompresses to
    /// <paramref name="uncompressedBytes"/> of zeros. Deflate takes that to about
    /// a thousandth of its size, which is the whole trick: the upload is tiny and
    /// well under MaxBytes, and what it turns into is not.
    /// </summary>
    private static byte[] ZipOfZeros(int uncompressedBytes)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = archive.CreateEntry("word/document.xml").Open())
            entry.Write(new byte[uncompressedBytes]);
        return buffer.ToArray();
    }

    [Fact]
    public void ZipBomb_IsRefusedBeforeAnythingIsDecompressed()
    {
        // The upload cap bounds what ARRIVES; it does not bound what a zip turns
        // into, and a .docx is a zip. Found in the Phase 4.5 review: nothing
        // between the 5 MB cap and OpenXml opening the package.
        var extractor = new DocumentTextExtractor(
            new DocumentOptions { MaxDecompressedBytes = 1024 * 1024 });

        var bomb = ZipOfZeros(4 * 1024 * 1024);

        // The point, in one assertion: the archive is a rounding error next to
        // what it claims to hold.
        Assert.True(bomb.Length < 64 * 1024, $"fixture was {bomb.Length} bytes");

        var result = extractor.Extract(bomb, "resume.docx");

        Assert.Equal(ResultStatus.Invalid, result.Status);
        Assert.Contains("unpacks to far more", result.Error);
    }

    [Fact]
    public void AnOrdinaryDocx_IsNotMistakenForABomb()
    {
        // The guard is worthless if it also refuses real documents, and the real
        // document is the checked-in fixture rather than something synthesised to
        // pass. Runs at the shipped 64 MB ceiling, not a lowered one.
        var extracted = Extract("resume.docx");

        Assert.Equal(SourceFormat.Docx, extracted.Format);
        Assert.Contains("Jane Doe", extracted.Text);
    }
}
