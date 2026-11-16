using System.Collections.Concurrent;
using System.Security.Cryptography;
using RagDotNet.Ocr;

namespace RagDotNet.Evaluation.Providers;

/// <summary>
/// Remembers what <paramref name="inner"/> read for each image, by content. Every configuration in a run ingests the
/// scanned set again, and the loader reads it once more, so this keeps a run to one OCR pass per page.
/// </summary>
public sealed class CachingOcrProvider(IOcrProvider inner) : IOcrProvider
{
    private readonly ConcurrentDictionary<string, Task<OcrResult>> _results = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Id => inner.Id;

    /// <inheritdoc />
    public Task<OcrResult> RecognizeAsync(ImageInput image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        var key = Convert.ToHexString(SHA256.HashData(image.Bytes.Span));
        return _results.GetOrAdd(key, _ => inner.RecognizeAsync(image, cancellationToken));
    }
}
