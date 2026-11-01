namespace RagDotNet.Core.Tests.Fakes;

/// <summary>A clock that moves only when the test advances it.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    /// <summary>Moves the clock by <paramref name="by"/>; a negative value steps it backwards (an NTP correction).</summary>
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
