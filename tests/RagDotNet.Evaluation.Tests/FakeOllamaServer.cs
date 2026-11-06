using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RagDotNet.Evaluation.Tests;

/// <summary>
/// A minimal stand-in for Ollama on a loopback port: <c>/api/tags</c> lists <paramref name="models"/>, and
/// <c>/api/embed</c> returns <paramref name="dimensions"/>-long vectors until <paramref name="failEmbedAfter"/> calls,
/// then answers 500.
/// </summary>
internal sealed class FakeOllamaServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string[] _models;
    private readonly int _dimensions;
    private readonly int _failEmbedAfter;
    private readonly Task _loop;
    private int _embedCalls;

    public FakeOllamaServer(string[] models, int dimensions = 4, int failEmbedAfter = int.MaxValue)
    {
        _models = models;
        _dimensions = dimensions;
        _failEmbedAfter = failEmbedAfter;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://localhost:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public string Url { get; }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            await HandleAsync(context).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath;
        string body;
        if (path == "/api/tags")
        {
            body = JsonSerializer.Serialize(new { models = _models.Select(m => new { name = m, model = m }) });
        }
        else if (path == "/api/embed" && Interlocked.Increment(ref _embedCalls) <= _failEmbedAfter)
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.InputStream).ConfigureAwait(false);
            var count = doc.RootElement.GetProperty("input").GetArrayLength();
            var vectors = Enumerable.Range(0, count).Select(i => Enumerable.Range(0, _dimensions).Select(d => d == i % _dimensions ? 1f : 0f));
            body = JsonSerializer.Serialize(new { model = "fake", embeddings = vectors });
        }
        else
        {
            context.Response.StatusCode = path == "/api/embed" ? 500 : 404;
            body = """{"error":"fake failure"}""";
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        await _loop.ConfigureAwait(false);
    }
}
