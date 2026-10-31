using Microsoft.Extensions.AI;

namespace RagDotNet.Core.Tests.Fakes;

/// <summary>Wraps an embedder; awaits <see cref="Gate"/> before embedding any batch the predicate selects.</summary>
public sealed class GatedEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, Func<IReadOnlyList<string>, bool> shouldPause)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when a selected batch first reaches the gate.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Completed by the test to let paused batches continue.</summary>
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = values.ToList();
        if (shouldPause(list))
        {
            _reached.TrySetResult();
            await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await inner.GenerateAsync(list, options, cancellationToken).ConfigureAwait(false);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();
}
