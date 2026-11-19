namespace Belot.WebApi;

internal sealed record TeamMatchPublicEventReplay(
    int LastEventSequence,
    bool SnapshotRequired,
    IReadOnlyList<PublicGameEventDto> Events);

internal sealed class TeamMatchPublicEventJournal
{
    private readonly Queue<PublicGameEventDto> events = new();

    private readonly int capacity;

    private int lastEventSequence = -1;

    public TeamMatchPublicEventJournal(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
    }

    public int LastEventSequence => this.lastEventSequence;

    public int NextSequence => checked(this.lastEventSequence + 1);

    public void Append(IReadOnlyList<PublicGameEventDto> events)
    {
        foreach (var gameEvent in events)
        {
            var sequenced = gameEvent with { Sequence = this.NextSequence };
            this.events.Enqueue(sequenced);
            this.lastEventSequence = sequenced.Sequence;
            if (this.events.Count > this.capacity)
            {
                this.events.Dequeue();
            }
        }
    }

    public TeamMatchPublicEventReplay GetEventsAfter(int afterSequence)
    {
        if (afterSequence < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }

        var firstRetainedSequence = this.events.Count == 0
            ? this.lastEventSequence + 1
            : this.events.Peek().Sequence;
        var snapshotRequired = afterSequence > this.lastEventSequence
            || afterSequence < firstRetainedSequence - 1;
        return new TeamMatchPublicEventReplay(
            this.lastEventSequence,
            snapshotRequired,
            snapshotRequired
                ? Array.Empty<PublicGameEventDto>()
                : this.events.Where(gameEvent => gameEvent.Sequence > afterSequence).ToArray());
    }
}
