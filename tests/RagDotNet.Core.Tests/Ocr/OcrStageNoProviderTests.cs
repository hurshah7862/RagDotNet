using RagDotNet.Documents;
using RagDotNet.Ocr;

namespace RagDotNet.Core.Tests.Ocr;

public class OcrStageNoProviderTests
{
    private static readonly ImageInput Image = new(new byte[] { 1, 2, 3 }, "image/png");

    [Fact]
    public void Fingerprint_is_none_without_providers() =>
        Assert.Equal("none", new OcrStage(null, null, [], new OcrOptions()).Fingerprint);

    [Fact]
    public async Task Candidates_become_the_stage5_warnings_in_page_order()
    {
        var parsed = await Read(
        [
            new OcrCandidate(OcrCandidateKind.ScannedPage, 0, 2),
            new OcrCandidate(OcrCandidateKind.Image, 0, 3, Image),
            new OcrCandidate(OcrCandidateKind.Image, 0, 3, Image),
            new OcrCandidate(OcrCandidateKind.Image, 0, 5, null),
        ]);

        Assert.Equal(
            [
                new IngestWarning(IngestWarningCodes.PageWithoutText, "Page 2 has no extractable text; register an OCR provider to read it.", 2),
                new IngestWarning(IngestWarningCodes.ImageNotRead, "Page 3 has 2 image(s) that were not read; register an OCR provider to read them.", 3),
                new IngestWarning(IngestWarningCodes.ImageNotRead, "Page 5 has 1 image(s) that were not read; register an OCR provider to read them.", 5),
            ],
            parsed.Warnings);
    }

    [Fact]
    public async Task Images_without_a_page_give_one_document_warning()
    {
        var parsed = await Read([new OcrCandidate(OcrCandidateKind.Image, 0, null, Image), new OcrCandidate(OcrCandidateKind.Image, 1, null, Image)]);

        Assert.Equal([new IngestWarning(IngestWarningCodes.ImageNotRead, "2 embedded image(s) were not read; register an OCR provider to read them.")], parsed.Warnings);
    }

    [Fact]
    public async Task Background_and_stamp_candidates_add_no_extra_warnings()
    {
        var parsed = await Read(
        [
            new OcrCandidate(OcrCandidateKind.ScannedPage, 1, 1) { HasTextLayer = true },
            new OcrCandidate(OcrCandidateKind.Image, 1, 1, Image) { IsPageBackground = true },
        ]);

        // Stage 5 counted every image on a page with text, the background included; a stamp page had no PageWithoutText.
        Assert.Equal([new IngestWarning(IngestWarningCodes.ImageNotRead, "Page 1 has 1 image(s) that were not read; register an OCR provider to read them.", 1)], parsed.Warnings);
    }

    [Fact]
    public async Task Reader_warnings_are_kept_and_candidates_stay_on_the_result()
    {
        var reader = new FixedReader(new ParsedDocument([new DocumentBlock(BlockKind.Paragraph, "x", [])])
        {
            Warnings = [new IngestWarning("Other", "from the reader")],
            OcrCandidates = [new OcrCandidate(OcrCandidateKind.ScannedPage, 1, 2)],
        });

        var parsed = await new OcrStage(null, null, [], new OcrOptions()).ReadAsync(reader, [], new DocumentReadContext("a.pdf", null), TestContext.Current.CancellationToken);

        Assert.Equal(["Other", IngestWarningCodes.PageWithoutText], parsed.Warnings.Select(w => w.Code));
        Assert.Single(parsed.OcrCandidates);
        Assert.Equal(["x"], parsed.Blocks.Select(b => b.Text));
    }

    private static Task<ParsedDocument> Read(IReadOnlyList<OcrCandidate> candidates) =>
        new OcrStage(null, null, [], new OcrOptions()).ReadAsync(
            new FixedReader(new ParsedDocument([new DocumentBlock(BlockKind.Paragraph, "x", [])]) { OcrCandidates = candidates }),
            [], new DocumentReadContext("a.pdf", null), TestContext.Current.CancellationToken);

    private sealed class FixedReader(ParsedDocument document) : IDocumentReader
    {
        public bool CanRead(string? contentType, string fileName) => true;

        public Task<ParsedDocument> ReadAsync(Stream stream, DocumentReadContext context, CancellationToken cancellationToken) => Task.FromResult(document);
    }
}
