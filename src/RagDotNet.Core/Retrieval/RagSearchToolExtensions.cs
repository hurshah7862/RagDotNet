using RagDotNet.Pipeline;

namespace RagDotNet.Retrieval;

/// <summary>Creates <see cref="RagSearchTool"/> instances.</summary>
public static class RagSearchToolExtensions
{
    /// <summary>
    /// Returns a tool a chat model can call to search the documents through this pipeline, under its access scope. Add it
    /// to <c>ChatOptions.Tools</c> on a chat client that invokes functions, such as one built with
    /// <c>UseFunctionInvocation()</c>. Create a new tool for each conversation or request, from the pipeline for that
    /// request's user.
    /// </summary>
    /// <param name="pipeline">The pipeline to search.</param>
    /// <param name="options">The tool's name, description and retrieval options.</param>
    /// <returns>The tool.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pipeline"/> is null.</exception>
    /// <exception cref="ArgumentException">The name is not 1 to 64 letters, digits, underscores or hyphens, or the description is empty.</exception>
    public static RagSearchTool AsSearchTool(this IRagPipeline pipeline, RagSearchToolOptions? options = null) =>
        new(pipeline, options);
}
