using RagDotNet.Access;
using RagDotNet.Documents;
using RagDotNet.Pipeline;

namespace RagDotNet.Hosting;

/// <summary>The scoped facade: captures and checks the caller's scope, then hands the job to the registered <see cref="IIngestionJobQueue"/>.</summary>
internal sealed class IngestionQueue(IIngestionJobQueue jobs, IRagAdmin admin, IAccessScope? accessScope) : IIngestionQueue
{
    public async ValueTask<string> EnqueueAsync(DocumentSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var scope = accessScope is null ? null : await accessScope.GetScopeAsync(cancellationToken).ConfigureAwait(false);
        if (scope is not null)
        {
            admin.ForScope(scope); // validates the scope now, so a malformed one never reaches the queue
            if (!scope.Permissions.HasFlag(RagPermissions.Write))
                throw new RagAccessDeniedException("enqueue", RagPermissions.Write, "enqueue: The access scope does not grant Write.");
        }

        var id = Guid.NewGuid().ToString("N");
        await jobs.EnqueueAsync(new IngestionJob(id, source, scope), cancellationToken).ConfigureAwait(false);
        return id;
    }

    public ValueTask<IngestionJobStatus?> GetStatusAsync(string jobId, CancellationToken cancellationToken = default) =>
        jobs.GetStatusAsync(jobId, cancellationToken);

    public ValueTask<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default) => jobs.CancelAsync(jobId, cancellationToken);

    public ValueTask PauseAsync(CancellationToken cancellationToken = default) => jobs.PauseAsync(cancellationToken);

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => jobs.ResumeAsync(cancellationToken);
}
