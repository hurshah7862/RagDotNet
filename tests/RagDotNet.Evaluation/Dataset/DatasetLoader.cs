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

    /// <summary>
    /// Loads the dataset sets under <paramref name="root"/>: the root itself when it holds <c>corpus/</c> (one set named
    /// after the directory), otherwise each subdirectory holding <c>corpus/</c>, in ordinal name order.
    /// </summary>
    /// <exception cref="DatasetValidationException">Any set breaks a rule, or ids repeat across sets; every problem is listed.</exception>
    public static async Task<IReadOnlyList<DatasetSet>> LoadSetsAsync(string root, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var subsets = Directory.Exists(full)
            ? Directory.GetDirectories(full).Where(d => Directory.Exists(Path.Combine(d, "corpus"))).Order(StringComparer.Ordinal).ToList()
            : [];
        var isSet = Directory.Exists(Path.Combine(full, "corpus"));
        if (isSet && subsets.Count > 0)
        {
            // Usually a stale build output still holding an older layout; refuse rather than guess which to use.
            throw new DatasetValidationException([$"{full} holds both corpus/ and dataset set directories; remove one"]);
        }

        if (!isSet && subsets.Count == 0)
        {
            throw new DatasetValidationException([$"no dataset found in {full}: expected corpus/ and questions.json, or set directories holding them"]);
        }

        var errors = new List<string>();
        var sets = new List<DatasetSet>();
        foreach (var dir in isSet ? new List<string> { full } : subsets)
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, Running.ReportWriter.AllSection, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"set name '{name}' is reserved for the report section that pools every set; rename the directory");
                continue;
            }

            try
            {
                sets.Add(new DatasetSet(name, await LoadAsync(dir, ct).ConfigureAwait(false)));
            }
            catch (DatasetValidationException ex)
            {
                errors.AddRange(ex.Errors.Select(e => $"{name}: {e}"));
            }
        }

        Duplicates(sets, s => s.Dataset.Documents.Select(d => d.DocumentId), "document id", errors);
        Duplicates(sets, s => s.Dataset.Questions.Select(q => q.Id), "question id", errors);
        return errors.Count > 0 ? throw new DatasetValidationException(errors) : sets;
    }

    private static void Duplicates(List<DatasetSet> sets, Func<DatasetSet, IEnumerable<string>> ids, string what, List<string> errors)
    {
        var seen = sets.SelectMany(s => ids(s).Distinct(StringComparer.Ordinal).Select(id => (Id: id, Set: s.Name)));
        foreach (var g in seen.GroupBy(x => x.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"{what} '{g.Key}' appears in sets {string.Join(" and ", g.Select(x => x.Set))}");
        }
    }

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
        var blocks = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var headings = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            var parsed = await reader.ReadAsync(stream, new DocumentReadContext(Path.GetFileName(file), null), ct).ConfigureAwait(false);
            var text = TextNormalizer.Normalize(string.Join(' ', parsed.Blocks.Select(b => b.Text)));
            var id = Path.GetFileNameWithoutExtension(file);
            documents.Add(new CorpusDocument(id, file, text));
            // Chunk text never includes headings, so only body blocks can hold scorable evidence.
            blocks[id] = [.. parsed.Blocks.Where(b => b.Kind != BlockKind.Heading).Select(b => TextNormalizer.Normalize(b.Text))];
            headings[id] = [.. parsed.Blocks.Where(b => b.Kind == BlockKind.Heading).Select(b => TextNormalizer.Normalize(b.Text))];
        }

        var questionsBytes = await File.ReadAllBytesAsync(questionsPath, ct).ConfigureAwait(false);
        List<Question?> questions;
        try
        {
            questions = JsonSerializer.Deserialize<List<Question?>>(questionsBytes) ?? [];
        }
        catch (JsonException ex)
        {
            throw new DatasetValidationException([$"questions.json is not valid: {ex.Message}"]);
        }

        var errors = Validate(documents, blocks, headings, questions);
        if (errors.Count > 0)
        {
            throw new DatasetValidationException(errors);
        }

        return new Dataset(documents, [.. questions.Select(q => q!)], await HashAsync(files, questionsBytes, ct).ConfigureAwait(false));
    }

    private static List<string> Validate(
        List<CorpusDocument> documents, Dictionary<string, string[]> blocks, Dictionary<string, string[]> headings, List<Question?> questions)
    {
        var errors = new List<string>();
        for (var i = 0; i < questions.Count; i++)
        {
            if (questions[i] is null) errors.Add($"question #{i + 1}: entry is null");
        }

        if (errors.Count > 0) return errors;
        if (documents.Count == 0) errors.Add("corpus has no .md files");
        if (questions.Count == 0) errors.Add("questions.json has no questions");

        var byId = documents.ToDictionary(d => d.DocumentId, StringComparer.Ordinal);
        foreach (var dup in questions.GroupBy(q => q!.Id ?? "", StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"{dup.Key}: duplicate question id ({dup.Count()} questions)");
        }

        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i]!;
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
            else if (headings[q.DocumentId].Any(h => h.Contains(evidence, StringComparison.Ordinal)))
            {
                errors.Add($"{q.Id}: evidence is in a heading of {q.DocumentId}; chunk text does not include headings, so pick a fragment from the section body");
            }
            else if (!blocks[q.DocumentId].Any(b => b.Contains(evidence, StringComparison.Ordinal)))
            {
                // A chunk can end at any block boundary, so evidence crossing one could never be scored as found.
                errors.Add($"{q.Id}: evidence spans more than one block of {q.DocumentId}; pick a fragment inside one paragraph, list, table or code block");
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
