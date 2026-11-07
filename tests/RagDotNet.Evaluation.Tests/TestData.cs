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

    /// <summary>A valid two-document, three-question dataset whose questions share wording with their evidence.</summary>
    public static string SmallDataset() => WriteDataset(
        new Dictionary<string, string>
        {
            ["garden"] = """
                # Garden guide

                ## Watering

                Water tomato plants deeply twice a week in summer. Shallow daily watering keeps roots near the surface.

                ## Soil

                Mix compost into clay soil before planting so that drainage improves over the season.
                """,
            ["kitchen"] = """
                # Kitchen guide

                ## Bread

                Bake sourdough bread at a high oven temperature with steam for the first twenty minutes.

                ## Storage

                Store fresh herbs upright in a glass of water inside the refrigerator door.
                """,
        },
        """
        [
          { "id": "w1", "question": "How often should I water tomato plants?", "documentId": "garden", "evidence": "water tomato plants deeply twice a week" },
          { "id": "s1", "question": "What should I mix into clay soil?", "documentId": "garden", "evidence": "mix compost into clay soil before planting" },
          { "id": "h1", "question": "How do I store fresh herbs?", "documentId": "kitchen", "evidence": "store fresh herbs upright in a glass of water" }
        ]
        """);

    /// <summary>Writes a dataset root holding one subdirectory per named set.</summary>
    public static string WriteSets(params (string Name, IReadOnlyDictionary<string, string> Documents, string QuestionsJson)[] sets)
    {
        var root = Path.Combine(Path.GetTempPath(), "ragdotnet-eval-sets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var (name, documents, questions) in sets)
        {
            Directory.Move(WriteDataset(documents, questions), Path.Combine(root, name));
        }

        return root;
    }

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
