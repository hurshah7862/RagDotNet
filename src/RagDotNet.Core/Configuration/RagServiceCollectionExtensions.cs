using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using RagDotNet.Access;
using RagDotNet.Configuration;
using RagDotNet.Content;
using RagDotNet.Documents;
using RagDotNet.Pipeline;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Dependency injection registration for RagDotNet.</summary>
public static class RagServiceCollectionExtensions
{
    /// <summary>
    /// Registers RagDotNet. Requires an <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> and a <see cref="VectorStore"/>
    /// in the container; an <see cref="IChatClient"/> is optional (needed only to answer questions).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options callback; options can also be bound with <c>services.Configure&lt;RagOptions&gt;(configuration)</c>.</param>
    /// <returns>A builder for readers, the access scope and external content.</returns>
    public static IRagBuilder AddRag(this IServiceCollection services, Action<RagOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registrations = services.FirstOrDefault(d => d.ServiceType == typeof(RagRegistrations))?.ImplementationInstance as RagRegistrations;
        if (registrations is null)
        {
            registrations = new RagRegistrations();
            services.AddSingleton(registrations);
        }

        var optionsBuilder = services.AddOptions<RagOptions>();
        if (configure is not null) optionsBuilder.Configure(configure);
        optionsBuilder.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RagOptions>, RagOptionsValidator>());

        services.TryAddSingleton(sp => RagEngineFactory.Create(
            sp.GetRequiredService<IOptions<RagOptions>>().Value,
            sp.GetServices<IDocumentReader>(),
            sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            sp.GetRequiredService<VectorStore>(),
            sp.GetService<ILoggerFactory>(),
            sp.GetService<IChunkContentStore>()));
        services.TryAddScoped<IRagPipeline>(sp => new RagPipeline(
            sp.GetRequiredService<RagEngine>(), sp.GetService<IAccessScope>(), sp.GetService<IChatClient>()));
        services.TryAddSingleton<IRagAdmin>(sp => new RagAdmin(sp.GetRequiredService<RagEngine>()));
        return new RagBuilder(services, registrations);
    }

    private sealed class RagBuilder(IServiceCollection services, RagRegistrations registrations) : IRagBuilder
    {
        public IServiceCollection Services => services;

        public IRagBuilder AddReader<TReader>() where TReader : class, IDocumentReader
        {
            services.AddSingleton<IDocumentReader, TReader>();
            return this;
        }

        public IRagBuilder AddAccessScope<TScope>() where TScope : class, IAccessScope
        {
            services.AddScoped<IAccessScope, TScope>();
            registrations.HasAccessScope = true;
            return this;
        }

        public IRagBuilder UseExternalContent<TStore>() where TStore : class, IChunkContentStore
        {
            services.AddSingleton<IChunkContentStore, TStore>();
            services.Configure<RagOptions>(o => o.ContentStorage = ContentStorageMode.External);
            registrations.HasContentStore = true;
            return this;
        }
    }
}
