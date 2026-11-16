using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RagDotNet.Configuration;
using RagDotNet.Keyword;
using RagDotNet.Keyword.Lucene;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Registers the Lucene keyword index with dependency injection.</summary>
    public static class LuceneKeywordIndexExtensions
    {
        /// <summary>
        /// Uses <see cref="LuceneKeywordIndex"/> for hybrid search instead of the in-memory index. The host fails to start when
        /// the directory is not set or another process has it open. The container disposes the index.
        /// </summary>
        /// <param name="builder">The RagDotNet builder from <c>AddRag</c>.</param>
        /// <param name="configure">Sets the index directory.</param>
        /// <returns>The same builder.</returns>
        public static IRagBuilder AddLuceneKeywordIndex(this IRagBuilder builder, Action<LuceneKeywordIndexOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            builder.Services.AddOptions<LuceneKeywordIndexOptions>().Configure(configure).ValidateOnStart();
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<LuceneKeywordIndexOptions>, LuceneKeywordIndexOptionsValidator>());
            builder.Services.TryAddSingleton(sp => new LuceneKeywordIndex(sp.GetRequiredService<IOptions<LuceneKeywordIndexOptions>>().Value));
            builder.Services.Replace(ServiceDescriptor.Singleton<IKeywordIndex>(sp => sp.GetRequiredService<LuceneKeywordIndex>()));
            return builder;
        }
    }
}

namespace RagDotNet.Configuration
{
    /// <summary>Registers the Lucene keyword index with the pipeline builder.</summary>
    public static class LuceneKeywordIndexBuilderExtensions
    {
        /// <summary>
        /// Uses <see cref="LuceneKeywordIndex"/> for hybrid search instead of the in-memory index. The index holds its directory
        /// for the life of the process; to release it earlier, create it yourself, pass it to <c>UseKeywordIndex</c> and dispose it.
        /// </summary>
        /// <param name="builder">The pipeline builder.</param>
        /// <param name="configure">Sets the index directory.</param>
        /// <returns>The same builder.</returns>
        /// <exception cref="ArgumentException">The directory is not set.</exception>
        /// <exception cref="InvalidOperationException">Another process or index instance has the directory open.</exception>
        public static RagPipelineBuilder AddLuceneKeywordIndex(this RagPipelineBuilder builder, Action<LuceneKeywordIndexOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            var options = new LuceneKeywordIndexOptions();
            configure(options);
            return builder.UseKeywordIndex(new LuceneKeywordIndex(options));
        }
    }
}

namespace RagDotNet.Keyword.Lucene
{
    /// <summary>
    /// Fails host start when the directory is not set, or when the index cannot be opened because another process or
    /// instance has it open. The check opens the index and releases it at once; the container's instance opens it later.
    /// </summary>
    internal sealed class LuceneKeywordIndexOptionsValidator : IValidateOptions<LuceneKeywordIndexOptions>
    {
        public ValidateOptionsResult Validate(string? name, LuceneKeywordIndexOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.Directory)) return ValidateOptionsResult.Fail("LuceneKeywordIndexOptions.Directory is required.");
            try
            {
                new LuceneKeywordIndex(options).Dispose();
            }
            catch (InvalidOperationException ex) when (ex.InnerException is global::Lucene.Net.Store.LockObtainFailedException)
            {
                return ValidateOptionsResult.Fail(ex.Message);
            }

            return ValidateOptionsResult.Success;
        }
    }
}
