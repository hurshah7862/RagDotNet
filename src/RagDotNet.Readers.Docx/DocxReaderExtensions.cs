using RagDotNet.Configuration;
using RagDotNet.Readers.Docx;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the DOCX reader, with dependency injection or the pipeline builder.</summary>
public static class DocxReaderExtensions
{
    /// <summary>Adds <see cref="DocxReader"/> so <c>.docx</c> documents can be ingested.</summary>
    /// <param name="builder">The RagDotNet builder from <c>AddRag</c>.</param>
    /// <returns>The same builder.</returns>
    public static IRagBuilder AddDocxReader(this IRagBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddReader<DocxReader>();
    }

    /// <summary>Adds <see cref="DocxReader"/> so <c>.docx</c> documents can be ingested.</summary>
    /// <param name="builder">The pipeline builder.</param>
    /// <returns>The same builder.</returns>
    public static RagPipelineBuilder AddDocxReader(this RagPipelineBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddReader(new DocxReader());
    }
}
