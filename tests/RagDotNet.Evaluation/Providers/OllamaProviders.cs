using Microsoft.Extensions.AI;
using OllamaSharp;

namespace RagDotNet.Evaluation.Providers;

/// <summary>The Ollama embedding and chat clients for real mode.</summary>
public static class OllamaProviders
{
    /// <summary>How long the reachability check waits.</summary>
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Creates the embedding client.</summary>
    public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbedder(Uri url, string model) => new OllamaApiClient(url, model);

    /// <summary>Creates the chat client.</summary>
    public static IChatClient CreateChat(Uri url, string model) => new OllamaApiClient(url, model);

    /// <summary>
    /// Embeds <c>"ping"</c> within <see cref="PingTimeout"/>. Returns null on success, or a one-line reason on failure.
    /// A vector of the wrong length is a failure too, since every later write would be rejected.
    /// </summary>
    public static async Task<string?> PingAsync(IEmbeddingGenerator<string, Embedding<float>> embedder, int dimensions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PingTimeout);
        try
        {
            var result = await embedder.GenerateAsync(["ping"], cancellationToken: timeout.Token).ConfigureAwait(false);
            var length = result.Count == 0 ? 0 : result[0].Vector.Length;
            return length == dimensions ? null : $"the model returned {length} dimensions, expected {dimensions} (use --dimensions)";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"no response within {PingTimeout.TotalSeconds:0} seconds";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.InnerException is { } inner ? $"{ex.Message} ({inner.Message})" : ex.Message;
        }
    }
}
