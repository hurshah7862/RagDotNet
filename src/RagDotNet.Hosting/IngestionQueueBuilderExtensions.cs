using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RagDotNet.Access;
using RagDotNet.Configuration;
using RagDotNet.Pipeline;

namespace RagDotNet.Hosting;

/// <summary>Registers background ingestion.</summary>
public static class IngestionQueueBuilderExtensions
{
    /// <summary>
    /// Adds <see cref="IIngestionQueue"/> (scoped) and its worker. The default queue keeps jobs in memory and runs them on a hosted
    /// service; to keep jobs across restarts, register your own <see cref="IIngestionJobQueue"/> before calling this.
    /// </summary>
    /// <param name="builder">The RagDotNet builder.</param>
    /// <param name="configure">Optional options callback.</param>
    /// <returns>The builder.</returns>
    public static IRagBuilder AddIngestionQueue(this IRagBuilder builder, Action<IngestionQueueOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        var options = services.AddOptions<IngestionQueueOptions>();
        if (configure is not null) options.Configure(configure);
        options
            .Validate(
                o => o.Capacity >= 1 && o.Concurrency >= 1 && o.MaxAttempts >= 1 && o.RetryDelay >= TimeSpan.Zero && o.StatusRetention > TimeSpan.Zero,
                "IngestionQueueOptions: Capacity, Concurrency and MaxAttempts must be at least 1, RetryDelay must not be negative, and StatusRetention must be positive.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IngestionJobRunner>();
        services.TryAddSingleton<IIngestionJobQueue>(sp => new InMemoryIngestionJobQueue(
            sp.GetRequiredService<IngestionJobRunner>(),
            sp.GetRequiredService<IOptions<IngestionQueueOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<InMemoryIngestionJobQueue>() ?? NullLogger<InMemoryIngestionJobQueue>.Instance));
        services.TryAddScoped<IIngestionQueue>(sp => new IngestionQueue(
            sp.GetRequiredService<IIngestionJobQueue>(), sp.GetRequiredService<IRagAdmin>(), sp.GetService<IAccessScope>()));
        services.AddHostedService<QueueHostedService>();
        return builder;
    }
}
