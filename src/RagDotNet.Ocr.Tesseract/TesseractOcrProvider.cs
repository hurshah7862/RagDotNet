using TesseractOCR;
using TesseractOCR.Enums;

namespace RagDotNet.Ocr.Tesseract;

/// <summary>
/// An <see cref="IOcrProvider"/> built on Tesseract (through the TesseractOCR package). One engine is shared and calls are
/// taken one at a time, because a Tesseract engine is not thread-safe.
/// </summary>
public sealed class TesseractOcrProvider : IOcrProvider, IDisposable
{
    private readonly Engine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the engine.</summary>
    /// <param name="options">The language data folder and languages.</param>
    /// <exception cref="OcrUnavailableException">Tesseract or the language data is missing; the message says what to install.</exception>
    public TesseractOcrProvider(TesseractOcrOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Problem(options) is { } problem) throw new OcrUnavailableException(problem);
        try
        {
            TesseractNative.EnsureLoaded();
            _engine = new Engine(options.DataPath, options.Languages, EngineMode.Default);
        }
        catch (OcrUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            throw new OcrUnavailableException(TesseractNative.InstallHint(), ex);
        }
        catch (Exception ex)
        {
            throw new OcrUnavailableException($"Tesseract could not start with the language data in '{options.DataPath}' ({options.Languages}): {ex.Message}", ex);
        }

        Id = $"tesseract/{options.Languages}/{DataFingerprint(options)}";
    }

    /// <summary>
    /// A short fingerprint of the language files' sizes, so switching between tessdata_fast and tessdata_best (or updating
    /// them) changes <see cref="Id"/> and documents with scanned pages are read again. Moving the folder does not.
    /// </summary>
    private static string DataFingerprint(TesseractOcrOptions options)
    {
        var sizes = string.Join(";", options.Languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => $"{l}:{new FileInfo(Path.Combine(options.DataPath, l + ".traineddata")).Length}"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sizes)))[..8].ToLowerInvariant();
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public async Task<OcrResult> RecognizeAsync(ImageInput image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        var bytes = image.Bytes.ToArray();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var pix = TesseractOCR.Pix.Image.LoadFromMemory(bytes)
                    ?? throw new InvalidDataException($"The {image.MediaType} image could not be decoded.");
                using var page = _engine.Process(pix);
                return new OcrResult(page.Text ?? "", page.MeanConfidence);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _engine.Dispose();
        _gate.Dispose();
    }

    /// <summary>The first missing language file, as a message naming its full path and where to get it; null when all exist.</summary>
    internal static string? Problem(TesseractOcrOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Languages)) return "TesseractOcrOptions.Languages must name at least one language, such as eng.";
        foreach (var language in options.Languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var file = Path.Combine(options.DataPath, language + ".traineddata");
            if (!File.Exists(file))
                return $"Tesseract language data '{file}' was not found. Download {language}.traineddata from https://github.com/tesseract-ocr/tessdata_fast and set TesseractOcrOptions.DataPath to the folder that holds it.";
        }

        return null;
    }

    private static bool IsNativeLoadFailure(Exception ex) =>
        ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
        || (ex.InnerException is { } inner && IsNativeLoadFailure(inner));
}
