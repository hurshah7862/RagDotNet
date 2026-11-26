using RagDotNet.Generation;
using RagDotNet.Pipeline;

/// <summary>Reads questions from the console and prints each streamed answer with its citations, until an empty line.</summary>
internal static class QuestionLoop
{
    public static async Task RunAsync(IRagPipeline pipeline)
    {
        while (true)
        {
            Console.Write("\nQuestion (empty line to exit): ");
            string? question = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(question)) return;

            RagAnswer? final = null;
            await foreach (var update in pipeline.AskStreamingAsync(question))
            {
                if (update.Text is not null) Console.Write(update.Text);
                if (update.Final is not null) final = update.Final;
            }

            Console.WriteLine();
            if (final is null) continue;
            if (!final.IsAnswered) Console.WriteLine("(no answer found in the documents)");

            // Location says where the passage is: the page for a PDF, the heading path for other formats.
            foreach (var c in final.Citations)
            {
                Console.WriteLine($"[{c.Number}] {c.Chunk.SourceName} — {c.Location}");
            }
        }
    }
}
