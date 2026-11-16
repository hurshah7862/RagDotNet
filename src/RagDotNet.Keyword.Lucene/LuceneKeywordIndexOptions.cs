namespace RagDotNet.Keyword.Lucene;

/// <summary>Options for <see cref="LuceneKeywordIndex"/>.</summary>
public sealed class LuceneKeywordIndexOptions
{
    /// <summary>The folder holding the index; created when missing. Required. Only one process can write to it at a time.</summary>
    public string Directory { get; set; } = "";
}
