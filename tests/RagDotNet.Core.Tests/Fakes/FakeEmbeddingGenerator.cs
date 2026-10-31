using Microsoft.Extensions.AI;

namespace RagDotNet.Core.Tests.Fakes;

/// <summary>Deterministic bag-of-words embedder: words hash into buckets, then the vector is L2-normalised.</summary>
public sealed class FakeEmbeddingGenerator(int dimensions = 64, string modelId = "fake-embed")
    : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly EmbeddingGeneratorMetadata _metadata = new("fake", null, modelId, dimensions);

    public int CallCount { get; private set; }

    public List<string> Inputs { get; } = [];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            Inputs.Add(value);
            result.Add(new Embedding<float>(Embed(value)));
        }

        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(_metadata) ? _metadata
        : serviceKey is null && serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
    }

    private float[] Embed(string text)
    {
        var vector = new float[dimensions];
        foreach (var word in text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var hash = 2166136261u;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(word))
            {
                hash = (hash ^ b) * 16777619u;
            }

            vector[(int)(hash % (uint)dimensions)] += 1f;
        }

        var norm = Math.Sqrt(vector.Sum(x => (double)x * x));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(vector[i] / norm);
            }
        }

        return vector;
    }
}
