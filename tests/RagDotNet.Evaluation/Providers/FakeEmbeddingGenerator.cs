using System.Text;
using Microsoft.Extensions.AI;

namespace RagDotNet.Evaluation.Providers;

/// <summary>
/// Deterministic hashed bag-of-words embedder: each <c>[a-z0-9]+</c> token of the lower-cased text adds 1 to the bucket
/// given by its FNV-1a hash, then the vector is L2-normalized. Text with no tokens embeds to the zero vector.
/// </summary>
public sealed class FakeEmbeddingGenerator(int dimensions = 256) : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>The model id reported in metadata and the report.</summary>
    public const string ModelId = "fake-embed";

    private readonly EmbeddingGeneratorMetadata _metadata = new("fake", null, ModelId, dimensions);

    /// <inheritdoc />
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new Embedding<float>(Embed(value)));
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(_metadata) ? _metadata
        : serviceKey is null && serviceType.IsInstanceOfType(this) ? this
        : null;

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private float[] Embed(string text)
    {
        var vector = new float[dimensions];
        var token = new StringBuilder();
        foreach (var c in text.ToLowerInvariant().Append(' '))
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                token.Append(c);
                continue;
            }

            if (token.Length > 0)
            {
                vector[Bucket(token.ToString())] += 1f;
                token.Clear();
            }
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

    private int Bucket(string token)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(token))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return (int)(hash % (uint)dimensions);
    }
}
