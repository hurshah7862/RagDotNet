using RagDotNet.Evaluation.Datasets;
using RagDotNet.Retrieval;

namespace RagDotNet.Evaluation.Tests;

internal static class TestData
{
    public static RetrievedChunk Chunk(string documentId, string text) =>
        new($"{documentId}#0", documentId, 0, text, 1.0, ScoreKind.Similarity, null, null, [], documentId,
            new Dictionary<string, object?>());

    public static Question Q(string documentId, string evidence, string id = "q") =>
        new(id, "question?", documentId, evidence);

    /// <summary>Writes a dataset directory (corpus/*.md and questions.json) under a fresh temp folder.</summary>
    public static string WriteDataset(IReadOnlyDictionary<string, string> documents, string questionsJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ragdotnet-eval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "corpus"));
        foreach (var (name, text) in documents)
        {
            File.WriteAllText(Path.Combine(dir, "corpus", name + ".md"), text);
        }

        File.WriteAllText(Path.Combine(dir, "questions.json"), questionsJson);
        return dir;
    }
}
