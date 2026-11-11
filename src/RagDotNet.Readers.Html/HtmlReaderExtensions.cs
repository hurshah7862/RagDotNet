using RagDotNet.Configuration;
using RagDotNet.Readers.Html;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Registers the HTML reader with dependency injection.</summary>
    public static class HtmlReaderExtensions
    {
        /// <summary>Adds <see cref="HtmlReader"/> so <c>.html</c>, <c>.htm</c> and <c>.xhtml</c> documents can be ingested.</summary>
        /// <param name="builder">The RagDotNet builder from <c>AddRag</c>.</param>
        /// <returns>The same builder.</returns>
        public static IRagBuilder AddHtmlReader(this IRagBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.AddReader<HtmlReader>();
        }
    }
}

namespace RagDotNet.Configuration
{
    /// <summary>Registers the HTML reader with the pipeline builder.</summary>
    public static class HtmlReaderBuilderExtensions
    {
        /// <summary>Adds <see cref="HtmlReader"/> so <c>.html</c>, <c>.htm</c> and <c>.xhtml</c> documents can be ingested.</summary>
        /// <param name="builder">The pipeline builder.</param>
        /// <returns>The same builder.</returns>
        public static RagPipelineBuilder AddHtmlReader(this RagPipelineBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.AddReader(new HtmlReader());
        }
    }
}
