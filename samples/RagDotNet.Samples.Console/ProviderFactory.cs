using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OllamaSharp;
using OpenAI;

/// <summary>Creates the chat client and embedding generator for the provider chosen in configuration.</summary>
internal static class ProviderFactory
{
    public static (IChatClient Chat, IEmbeddingGenerator<string, Embedding<float>> Embedder, int Dimensions) Create(IConfiguration config)
    {
        string provider = config["Provider"] ?? "Ollama";
        switch (provider.ToLowerInvariant())
        {
            case "ollama":
            {
                var s = config.GetSection("Ollama");
                var endpoint = new Uri(s["Endpoint"] ?? "http://127.0.0.1:11434");
                IChatClient chat = new OllamaApiClient(endpoint, s["ChatModel"] ?? "llama3.2:3b");
                IEmbeddingGenerator<string, Embedding<float>> embedder =
                    new OllamaApiClient(endpoint, s["EmbeddingModel"] ?? "nomic-embed-text");
                return (chat, embedder, s.GetValue("Dimensions", 768));
            }
            case "githubmodels":
                return CreateOpenAi(config.GetSection("GitHubModels"), "GITHUB_TOKEN", "https://models.github.ai/inference");
            case "openai":
                return CreateOpenAi(config.GetSection("OpenAI"), "OPENAI_API_KEY", null);
            default:
                throw new InvalidOperationException($"Unknown Provider '{provider}'. Use Ollama, GitHubModels or OpenAI.");
        }
    }

    private static (IChatClient, IEmbeddingGenerator<string, Embedding<float>>, int) CreateOpenAi(
        IConfigurationSection s, string tokenVariable, string? defaultEndpoint)
    {
        string? token = Environment.GetEnvironmentVariable(tokenVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"Environment variable {tokenVariable} is not set. Set it to use this provider.");
        }

        var options = new OpenAIClientOptions();
        string? endpoint = s["Endpoint"] ?? defaultEndpoint;
        if (!string.IsNullOrEmpty(endpoint)) options.Endpoint = new Uri(endpoint);
        var client = new OpenAIClient(new ApiKeyCredential(token), options);
        return (client.GetChatClient(s["ChatModel"] ?? "gpt-4o-mini").AsIChatClient(),
            client.GetEmbeddingClient(s["EmbeddingModel"] ?? "text-embedding-3-small").AsIEmbeddingGenerator(),
            s.GetValue("Dimensions", 1536));
    }
}
