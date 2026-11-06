using RagDotNet.Evaluation.Scoring;
using static RagDotNet.Evaluation.Tests.TestData;

namespace RagDotNet.Evaluation.Tests;

public class ScoringTests
{
    [Fact]
    public void Normalize_strips_markup_collapses_whitespace_and_lowercases() =>
        Assert.Equal("run quill init now", TextNormalizer.Normalize("  Run `quill   init`\n**now** "));

    [Fact]
    public void Summarize_computes_recall_and_mrr()
    {
        var s = Metrics.Summarize([1, 3, null, 6]);
        Assert.Equal(0.25, s.RecallAt1);
        Assert.Equal(0.5, s.RecallAt3);
        Assert.Equal(0.5, s.RecallAt5);
        Assert.Equal((1 + 1.0 / 3 + 0 + 1.0 / 6) / 4, s.Mrr, 10);
    }

    [Fact]
    public void Summarize_rejects_an_empty_list() =>
        Assert.Throws<ArgumentException>(() => Metrics.Summarize([]));

    [Fact] // Review Focus 2
    public void No_relevant_result_has_no_rank_and_zero_reciprocal()
    {
        Assert.Null(Metrics.RankOfFirstRelevant([Chunk("doc-a", "unrelated text")], Q("doc-a", "the answer is here")));
        var s = Metrics.Summarize([null]);
        Assert.Equal(0, s.Mrr);
        Assert.Equal(0, s.RecallAt5);
    }

    [Fact]
    public void Rank_is_the_one_based_position_of_the_first_relevant_chunk()
    {
        var q = Q("doc-a", "the answer is here");
        var results = new[]
        {
            Chunk("doc-a", "nothing"),
            Chunk("doc-b", "The answer is here"),
            Chunk("doc-a", "so: the answer is here."),
            Chunk("doc-a", "the answer is here"),
        };
        Assert.Equal(3, Metrics.RankOfFirstRelevant(results, q));
    }

    [Fact]
    public void Relevance_needs_same_document_and_contained_evidence()
    {
        var q = Q("doc-a", "set sync interval to 5 minutes");
        Assert.False(Relevance.IsRelevant(Chunk("doc-b", "Set sync interval to 5 minutes."), q));
        Assert.True(Relevance.IsRelevant(Chunk("doc-a", "You can **set sync\ninterval** to `5`   minutes here."), q));
        Assert.False(Relevance.IsRelevant(Chunk("doc-a", "set sync interval to 10 minutes"), q));
    }
}
