using RagDotNet.Configuration;
using RagDotNet.Readers.Pdf.Rendering;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Registers PDF page rendering with dependency injection.</summary>
    public static class PdfPageRenderingExtensions
    {
        /// <summary>Adds <see cref="PdfPageRenderer"/>, so the per-image OCR provider can read scanned PDF pages.</summary>
        /// <param name="builder">The RagDotNet builder from <c>AddRag</c>.</param>
        /// <returns>The same builder.</returns>
        public static IRagBuilder AddPdfPageRendering(this IRagBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.AddPageRenderer<PdfPageRenderer>();
        }
    }
}

namespace RagDotNet.Configuration
{
    /// <summary>Registers PDF page rendering with the pipeline builder.</summary>
    public static class PdfPageRenderingBuilderExtensions
    {
        /// <summary>Adds <see cref="PdfPageRenderer"/>, so the per-image OCR provider can read scanned PDF pages.</summary>
        /// <param name="builder">The pipeline builder.</param>
        /// <returns>The same builder.</returns>
        public static RagPipelineBuilder AddPdfPageRendering(this RagPipelineBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.AddPageRenderer(new PdfPageRenderer());
        }
    }
}
