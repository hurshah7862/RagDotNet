using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RagDotNet.Documents;
using RagDotNet.Evaluation.Scoring;
using RagDotNet.Readers;

namespace RagDotNet.Evaluation.Datasets;

/// <summary>Loads <c>corpus/*.md</c> and <c>questions.json</c> from a dataset directory and validates them.</summary>
public static class DatasetLoader
{
    private const int MinEvidence = 20;
    private const int MaxEvidence = 80;

    /// <summary>Loads and validates the dataset.</summary>
    /// <exception cref="DatasetValidationException">The dataset breaks one or more rules; every problem is listed.</exception>
    public static async Task<Dataset> LoadAsync(string datasetDirectory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetDirectory);
        var corpusDir = Path.Combine(datasetDirectory, "corpus");
        var questionsPath = Path.Combine(datasetDirectory, "questions.json");
        if (!Directory.Exists(corpusDir) || !File.Exists(questionsPath))
        {
            throw new DatasetValidationException([$"dataset not found: expected {corpusDir} and {questionsPath}"]);
        }

        var files = Directory.GetFiles(corpusDir, "*.md").Order(StringComparer.Ordinal).ToList();
        var reader = new MarkdownReader();
        var documents = new List<CorpusDocument>();
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            var parsed = await reader.ReadAsync(stream, new DocumentReadContext(Path.GetFileName(file), null), ct).ConfigureAwait(false);
            var text = TextNormalizer.Normalize(string.Join(' ', parsed.Blocks.Select(b => b.Text)));
            documents.Add(new CorpusDocument(Path.GetFileNameWithoutExtension(file), file, text));
        }

        var questionsBytes = await File.ReadAllBytesAsync(questionsPath, ct).ConfigureAwait(false);
        List<Question> questions;
        try
        {
            questions = JsonSerializer.Deserialize<List<Question>>(questionsBytes) ?? [];
        }
        catch (JsonException ex)
        {
            throw new DatasetValidationException([$"questions.json is not valid: {ex.Message}"]);
        }

        var errors = Validate(documents, questions);
        if (errors.Count > 0)
        {
            throw new DatasetValidationException(errors);
        }

        return new Dataset(documents, questions, await HashAsync(files, questionsBytes, ct).ConfigureAwait(false));
    }

    private static List<string> Validate(List<CorpusDocument> documents, List<Question> questions)
    {
        var errors = new List<string>();
        if (documents.Count == 0) errors.Add("corpus has no .md files");
        if (questions.Count == 0) errors.Add("questions.json has no questions");

        var byId = documents.ToDictionary(d => d.DocumentId, StringComparer.Ordinal);
        foreach (var dup in questions.GroupBy(q => q.Id ?? "", StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"{dup.Key}: duplicate question id ({dup.Count()} questions)");
        }

        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            if (string.IsNullOrWhiteSpace(q.Id))
            {
                errors.Add($"question #{i + 1}: id is empty");
                continue;
            }

            if (string.IsNullOrWhiteSpace(q.Text)) errors.Add($"{q.Id}: question text is empty");

            var evidence = TextNormalizer.Normalize(q.Evidence ?? "");
            if (evidence.Length is < MinEvidence or > MaxEvidence)
            {
                errors.Add($"{q.Id}: evidence is {evidence.Length} characters after normalization; it must be {MinEvidence}-{MaxEvidence}");
                continue;
            }

            if (q.DocumentId is null || !byId.TryGetValue(q.DocumentId, out var labelled))
            {
                errors.Add($"{q.Id}: documentId '{q.DocumentId}' is not a corpus document");
                continue;
            }

            var total = documents.Sum(d => CountOccurrences(d.NormalizedText, evidence));
            if (CountOccurrences(labelled.NormalizedText, evidence) == 0)
            {
                errors.Add($"{q.Id}: evidence not found in {q.DocumentId}");
            }
            else if (total != 1)
            {
                errors.Add($"{q.Id}: evidence found {total} times");
            }
        }

        return errors;
    }

    /// <summary>Counts occurrences, including overlapping ones.</summary>
    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task<string> HashAsync(List<string> files, byte[] questionsBytes, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            // The file name is part of the hash so renaming a document changes it.
            sha.AppendData(Encoding.UTF8.GetBytes("corpus/" + Path.GetFileName(file) + "\n"));
            sha.AppendData(await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false));
        }

        sha.AppendData(Encoding.UTF8.GetBytes("questions.json\n"));
        sha.AppendData(questionsBytes);
        return Convert.ToHexStringLower(sha.GetHashAndReset())[..12];
    }
}
