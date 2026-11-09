using RagDotNet.Configuration;
using RagDotNet.Readers.Pdf;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the PDF reader, with dependency injection or the pipeline builder.</summary>
public static class PdfReaderExtensions
{
    /// <summary>Adds <see cref="PdfReader"/> so <c>.pdf</c> documents can be ingested.</summary>
    /// <param name="builder">The RagDotNet builder from <c>AddRag</c>.</param>
    /// <returns>The same builder.</returns>
    public static IRagBuilder AddPdfReader(this IRagBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddReader<PdfReader>();
    }

    /// <summary>Adds <see cref="PdfReader"/> so <c>.pdf</c> documents can be ingested.</summary>
    /// <param name="builder">The pipeline builder.</param>
    /// <returns>The same builder.</returns>
    public static RagPipelineBuilder AddPdfReader(this RagPipelineBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddReader(new PdfReader());
    }
}
