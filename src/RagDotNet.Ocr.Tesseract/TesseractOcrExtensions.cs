using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RagDotNet.Configuration;
using RagDotNet.Ocr;
using RagDotNet.Ocr.Tesseract;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Registers Tesseract OCR with dependency injection.</summary>
    public static class TesseractOcrExtensions
    {
        /// <summary>
        /// Adds <see cref="TesseractOcrProvider"/> as the OCR provider, and PDF page rendering so scanned pages can be read.
        /// A host fails at start when Tesseract or the language data is missing.
        /// </summary>
        /// <param name="builder">The RagDotNet builder from <c>AddRag</c>.</param>
        /// <param name="configure">Sets the language data folder and languages.</param>
        /// <returns>The same builder.</returns>
        public static IRagBuilder AddTesseractOcr(this IRagBuilder builder, Action<TesseractOcrOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var options = builder.Services.AddOptions<TesseractOcrOptions>();
            if (configure is not null) options.Configure(configure);
            options.ValidateOnStart();
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TesseractOcrOptions>, TesseractOcrOptionsValidator>());
            builder.Services.Replace(ServiceDescriptor.Singleton<IOcrProvider>(sp =>
                new TesseractOcrProvider(sp.GetRequiredService<IOptions<TesseractOcrOptions>>().Value)));
            return builder.AddPdfPageRendering();
        }
    }
}

namespace RagDotNet.Configuration
{
    /// <summary>Registers Tesseract OCR with the pipeline builder.</summary>
    public static class TesseractOcrBuilderExtensions
    {
        /// <summary>
        /// Adds <see cref="TesseractOcrProvider"/> as the OCR provider, and PDF page rendering so scanned pages can be read.
        /// </summary>
        /// <param name="builder">The pipeline builder.</param>
        /// <param name="configure">Sets the language data folder and languages.</param>
        /// <returns>The same builder.</returns>
        /// <exception cref="OcrUnavailableException">Tesseract or the language data is missing; the message says what to install.</exception>
        public static RagPipelineBuilder AddTesseractOcr(this RagPipelineBuilder builder, Action<TesseractOcrOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var options = new TesseractOcrOptions();
            configure?.Invoke(options);
            return builder.UseOcrProvider(new TesseractOcrProvider(options)).AddPdfPageRendering();
        }
    }
}

namespace RagDotNet.Ocr.Tesseract
{
    /// <summary>Fails host start when the language data is missing or Tesseract cannot be loaded.</summary>
    internal sealed class TesseractOcrOptionsValidator : IValidateOptions<TesseractOcrOptions>
    {
        public ValidateOptionsResult Validate(string? name, TesseractOcrOptions options)
        {
            if (TesseractOcrProvider.Problem(options) is { } problem) return ValidateOptionsResult.Fail(problem);
            try
            {
                TesseractNative.EnsureLoaded();
            }
            catch (OcrUnavailableException ex)
            {
                return ValidateOptionsResult.Fail(ex.Message);
            }

            return ValidateOptionsResult.Success;
        }
    }
}
