namespace Matigr.Checking.Domain;

// A local snapshot of an already accepted solution, not an intake aggregate.
public sealed record SolutionReference
{
    public Guid SolutionId { get; }
    public Guid TournamentId { get; }
    public Guid TeamId { get; }
    public Guid TaskId { get; }
    public Guid MaterialVersionId { get; }
    public string MaterialRef { get; }
    public DateTimeOffset AcceptedAt { get; }
    public long SourceVersion { get; }

    public SolutionReference(Guid solutionId, Guid tournamentId, Guid teamId, Guid taskId,
        Guid materialVersionId, string materialRef, DateTimeOffset acceptedAt, long sourceVersion)
    {
        if (new[] { solutionId, tournamentId, teamId, taskId, materialVersionId }.Contains(Guid.Empty))
            throw new ArgumentException("Snapshot identities must be present.");
        ArgumentException.ThrowIfNullOrWhiteSpace(materialRef);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceVersion);
        SolutionId = solutionId;
        TournamentId = tournamentId;
        TeamId = teamId;
        TaskId = taskId;
        MaterialVersionId = materialVersionId;
        MaterialRef = materialRef;
        AcceptedAt = acceptedAt.ToUniversalTime();
        SourceVersion = sourceVersion;
    }
}
