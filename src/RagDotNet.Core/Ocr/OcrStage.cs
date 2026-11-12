using System.Globalization;
using System.Security.Cryptography;
using RagDotNet.Diagnostics;
using RagDotNet.Documents;

namespace RagDotNet.Ocr;

/// <summary>
/// Reads a document and resolves its OCR candidates: recognised text is inserted where each scanned page or image was,
/// and whatever is not read becomes a warning. With no provider registered the warnings are exactly those the readers
/// reported before OCR existed. One failing page or image is a warning; <see cref="OcrUnavailableException"/> and
/// cancellation stop the ingest.
/// </summary>
internal sealed class OcrStage(IOcrProvider? images, IDocumentOcrProvider? documents, IReadOnlyList<IPageRenderer> renderers, OcrOptions options)
{
    private const string RegisterOcr = "register an OCR provider to read";
    private const string RegisterImageOcr = "register a per-image OCR provider (IOcrProvider) to read";

    /// <summary>Identifies the OCR setup; <c>none</c> without providers. Stored with documents that had OCR candidates.</summary>
    public string Fingerprint { get; } = images is null && documents is null
        ? "none"
        : string.Join("|",
            images?.Id ?? "-",
            documents is null ? "-" : $"{documents.Id}:{options.DocumentProviderMode}",
            string.Join(",", renderers.Select(r => r.GetType().FullName).Order(StringComparer.Ordinal)),
            options.OcrEmbeddedImages ? "images" : "no-images",
            options.MinImageSize.ToString(CultureInfo.InvariantCulture),
            options.PageDpi.ToString(CultureInfo.InvariantCulture));

    public async Task<ParsedDocument> ReadAsync(IDocumentReader reader, byte[] bytes, DocumentReadContext context, CancellationToken ct)
    {
        if (images is null && documents is null) return WithoutProviders(await ReadWithReaderAsync(reader, bytes, context, ct).ConfigureAwait(false));

        using var activity = RagTelemetry.ActivitySource.StartActivity("rag.ocr");
        var warnings = new List<IngestWarning>();

        if (documents is not null && options.DocumentProviderMode == DocumentOcrMode.ReplaceReader && documents.CanProcess(context.ContentType, context.FileName))
        {
            try
            {
                var replaced = await documents.ProcessAsync(bytes, context, ct).ConfigureAwait(false);
                return new ParsedDocument(replaced.Blocks) { Warnings = replaced.Warnings };
            }
            catch (Exception ex) when (IsItemFailure(ex))
            {
                warnings.Add(new IngestWarning(IngestWarningCodes.OcrFailed,
                    $"The document OCR provider failed on '{context.FileName}'; read with the built-in reader instead: {ex.Message}"));
            }
        }

        var parsed = await ReadWithReaderAsync(reader, bytes, context, ct).ConfigureAwait(false);
        warnings.InsertRange(0, parsed.Warnings);
        if (parsed.OcrCandidates.Count == 0) return parsed with { Warnings = warnings };

        var inserts = new List<(int InsertAt, int Order, IReadOnlyList<DocumentBlock> Blocks)>();
        var pages = parsed.OcrCandidates.Where(c => c.Kind == OcrCandidateKind.ScannedPage).ToList();
        var pending = pages.ToList();

        if (documents is not null && options.DocumentProviderMode == DocumentOcrMode.FillMissingPages && pages.Count > 0
            && documents.CanProcess(context.ContentType, context.FileName))
        {
            try
            {
                var filled = await documents.ProcessAsync(bytes, context, ct).ConfigureAwait(false);
                foreach (var page in pages)
                {
                    var path = PathAt(parsed.Blocks, page.InsertAt);
                    var blocks = filled.Blocks.Where(b => b.PageNumber == page.PageNumber && b.Text.Length > 0)
                        .Select(b => b with { HeadingPath = path }).ToList();
                    inserts.Add((page.InsertAt, Order(parsed, page), blocks));
                }

                warnings.AddRange(filled.Warnings);
                pending.Clear();
            }
            catch (Exception ex) when (IsItemFailure(ex))
            {
                warnings.Add(new IngestWarning(IngestWarningCodes.OcrFailed, $"The document OCR provider failed on '{context.FileName}': {ex.Message}"));
            }
        }

        var unread = new List<OcrCandidate>();
        var renderer = renderers.FirstOrDefault(r => r.CanRender(context.ContentType, context.FileName));
        foreach (var page in pending)
        {
            if (images is null)
            {
                unread.Add(page);
            }
            else if (renderer is null)
            {
                if (!page.HasTextLayer)
                    warnings.Add(new IngestWarning(IngestWarningCodes.PageWithoutText,
                        $"Page {page.PageNumber} has no extractable text; add RagDotNet.Readers.Pdf.Rendering (AddPdfPageRendering) so the OCR provider can read it.", page.PageNumber));
            }
            else if (page.PageNumber is { } number)
            {
                var text = await RecognizeAsync(page, () => renderer.RenderPageAsync(bytes, number, options.PageDpi, ct), warnings, ct).ConfigureAwait(false);
                if (text is not null) inserts.Add((page.InsertAt, Order(parsed, page), [Block(parsed.Blocks, page, text)]));
            }
        }

        var recognised = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in parsed.OcrCandidates.Where(c => c.Kind == OcrCandidateKind.Image))
        {
            if (images is null)
            {
                unread.Add(image);
                continue;
            }

            if (!options.OcrEmbeddedImages || image.IsPageBackground) continue;
            if (image.Image is not { } input)
            {
                warnings.Add(new IngestWarning(IngestWarningCodes.OcrFailed, $"{PagePrefix(image, capital: true)}n image could not be extracted for OCR.", image.PageNumber));
                continue;
            }

            if (TooSmall(input)) continue;
            if (!recognised.Add(Convert.ToHexString(SHA256.HashData(input.Bytes.Span)))) continue; // read once, placed at its first position

            var text = await RecognizeAsync(image, () => Task.FromResult(input), warnings, ct).ConfigureAwait(false);
            if (text is not null) inserts.Add((image.InsertAt, Order(parsed, image), [Block(parsed.Blocks, image, text)]));
        }

        warnings.AddRange(Unread(unread, documents is not null && images is null ? RegisterImageOcr : RegisterOcr));

        // Insert from the end so earlier positions stay valid; candidates at one position keep their document order.
        var blocksOut = parsed.Blocks.ToList();
        foreach (var (insertAt, _, blocks) in inserts.OrderByDescending(i => i.InsertAt).ThenByDescending(i => i.Order))
            blocksOut.InsertRange(Math.Clamp(insertAt, 0, blocksOut.Count), blocks);
        return parsed with { Blocks = blocksOut, Warnings = warnings };
    }

    private static async Task<ParsedDocument> ReadWithReaderAsync(IDocumentReader reader, byte[] bytes, DocumentReadContext context, CancellationToken ct)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return await reader.ReadAsync(stream, context, ct).ConfigureAwait(false);
    }

    private static ParsedDocument WithoutProviders(ParsedDocument parsed) =>
        parsed.OcrCandidates.Count == 0 ? parsed : parsed with { Warnings = [.. parsed.Warnings, .. Unread(parsed.OcrCandidates, RegisterOcr)] };

    /// <summary>Recognises one page or image; a failure becomes a warning and null, low confidence a warning.</summary>
    private async Task<string?> RecognizeAsync(OcrCandidate candidate, Func<Task<ImageInput>> image, List<IngestWarning> warnings, CancellationToken ct)
    {
        try
        {
            var result = await images!.RecognizeAsync(await image().ConfigureAwait(false), ct).ConfigureAwait(false);
            var text = result.Text.Trim();
            if (text.Length == 0) return null;
            if (result.Confidence < options.MinConfidence)
                warnings.Add(new IngestWarning(IngestWarningCodes.OcrLowConfidence,
                    $"{PagePrefix(candidate, capital: false)}OCR text has low confidence ({result.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}); it was kept.", candidate.PageNumber));
            return text;
        }
        catch (Exception ex) when (IsItemFailure(ex))
        {
            warnings.Add(new IngestWarning(IngestWarningCodes.OcrFailed, $"{PagePrefix(candidate, capital: false)}OCR failed: {ex.Message}", candidate.PageNumber));
            return null;
        }
    }

    private static bool IsItemFailure(Exception ex) => ex is not (OperationCanceledException or OcrUnavailableException);

    /// <summary>"Page 3: " before a message about a paged candidate; for an unpaged one, nothing (or "A" to start "An image…").</summary>
    private static string PagePrefix(OcrCandidate candidate, bool capital) =>
        candidate.PageNumber is { } page ? $"Page {page}: " + (capital ? "a" : "") : capital ? "A" : "";

    private bool TooSmall(ImageInput image)
    {
        int? width = image.Width, height = image.Height;
        if ((width is null || height is null) && ImageHeader.TryGetSize(image.Bytes.Span, out var w, out var h))
            (width, height) = (width ?? w, height ?? h);
        return width < options.MinImageSize || height < options.MinImageSize;
    }

    private static int Order(ParsedDocument parsed, OcrCandidate candidate) => parsed.OcrCandidates.ToList().IndexOf(candidate);

    private static IReadOnlyList<string> PathAt(IReadOnlyList<DocumentBlock> blocks, int insertAt) =>
        insertAt > 0 && insertAt <= blocks.Count ? blocks[insertAt - 1].HeadingPath : [];

    private static DocumentBlock Block(IReadOnlyList<DocumentBlock> blocks, OcrCandidate candidate, string text) =>
        new(BlockKind.Paragraph, text, PathAt(blocks, candidate.InsertAt), candidate.PageNumber);

    /// <summary>The stage 5 warnings for candidates left unread: one per scanned page, and image counts per page or per document.</summary>
    private static IEnumerable<IngestWarning> Unread(IEnumerable<OcrCandidate> candidates, string advice)
    {
        var list = candidates.ToList();
        var pages = list.Where(c => c.PageNumber is not null).Select(c => c.PageNumber!.Value).Distinct().Order();
        foreach (var page in pages)
        {
            if (list.Any(c => c.PageNumber == page && c.Kind == OcrCandidateKind.ScannedPage && !c.HasTextLayer))
                yield return new IngestWarning(IngestWarningCodes.PageWithoutText, $"Page {page} has no extractable text; {advice} it.", page);
            var count = list.Count(c => c.PageNumber == page && c.Kind == OcrCandidateKind.Image);
            if (count > 0)
                yield return new IngestWarning(IngestWarningCodes.ImageNotRead, $"Page {page} has {count} image(s) that were not read; {advice} them.", page);
        }

        var unpaged = list.Count(c => c.PageNumber is null && c.Kind == OcrCandidateKind.Image);
        if (unpaged > 0)
            yield return new IngestWarning(IngestWarningCodes.ImageNotRead, $"{unpaged} embedded image(s) were not read; {advice} them.");
    }
}
