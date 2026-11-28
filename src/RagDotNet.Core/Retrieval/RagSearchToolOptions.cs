namespace RagDotNet.Retrieval;

/// <summary>Options for <see cref="RagSearchTool"/>.</summary>
public sealed class RagSearchToolOptions
{
    /// <summary>The default tool name.</summary>
    public const string DefaultName = "search_documents";

    internal const string DefaultDescription =
        "Searches the user's documents and returns the most relevant passages, each with a number. " +
        "Use it whenever the answer may be in the documents. Write the query as a standalone search, not a follow-up. " +
        "Cite the passages you use with their numbers in square brackets, like [1].";

    /// <summary>
    /// The tool name the model calls: 1 to 64 letters, digits, underscores or hyphens. Defaults to <see cref="DefaultName"/>.
    /// Give each tool a different name when an app registers several, for example one per document collection.
    /// </summary>
    public string Name { get; set; } = DefaultName;

    /// <summary>
    /// The description the model reads to decide when to call the tool. The default says the tool searches the user's
    /// documents, asks for a standalone query and asks for <c>[n]</c> citations.
    /// Saying what the documents cover ("Searches the HR policies") helps the model call it at the right time.
    /// </summary>
    public string Description { get; set; } = DefaultDescription;

    /// <summary>Retrieval options for every search the tool makes. Null uses the <see cref="RetrievalOptions"/> defaults.</summary>
    public RetrievalOptions? Retrieval { get; set; }
}
