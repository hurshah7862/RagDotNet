using RagDotNet.Documents;

namespace RagDotNet.Ocr;

/// <summary>
/// Reads a document and resolves its OCR candidates: recognised text is inserted where each scanned page or image was,
/// and whatever is not read becomes a warning. With no provider registered the warnings are exactly those the readers
/// reported before OCR existed.
/// </summary>
internal sealed class OcrStage(IOcrProvider? images, IDocumentOcrProvider? documents, IReadOnlyList<IPageRenderer> renderers, OcrOptions options)
{
    private const string RegisterOcr = "register an OCR provider to read";

    /// <summary>Identifies the OCR setup; <c>none</c> without providers. Stored with documents that had OCR candidates.</summary>
    public string Fingerprint { get; } = images is null && documents is null
        ? "none"
        : string.Join("|",
            images?.Id ?? "-",
            documents is null ? "-" : $"{documents.Id}:{options.DocumentProviderMode}",
            string.Join(",", renderers.Select(r => r.GetType().FullName).Order(StringComparer.Ordinal)),
            options.OcrEmbeddedImages ? "images" : "no-images",
            options.MinImageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            options.PageDpi.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public async Task<ParsedDocument> ReadAsync(IDocumentReader reader, byte[] bytes, DocumentReadContext context, CancellationToken ct)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var parsed = await reader.ReadAsync(stream, context, ct).ConfigureAwait(false);
        if (parsed.OcrCandidates.Count == 0) return parsed;

        var warnings = new List<IngestWarning>(parsed.Warnings);
        warnings.AddRange(Unread(parsed.OcrCandidates, RegisterOcr));
        return parsed with { Warnings = warnings };
    }

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
