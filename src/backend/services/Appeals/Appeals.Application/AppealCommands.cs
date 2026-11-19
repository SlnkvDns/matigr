namespace Matigr.Appeals.Application;

public sealed record AppealActor(Guid SubjectId, IReadOnlyCollection<string> Roles, IReadOnlyCollection<Guid> Tournaments, Guid? TeamId)
{
    public bool Has(string role, Guid tournament) => SubjectId != Guid.Empty && Roles.Contains(role) && Tournaments.Contains(tournament);
}
public sealed record AppealReply(int Status, string Json, string? ETag = null, string? Location = null);
public interface IManualEnd
{
    Task<AppealReply> RecordAsync(AppealActor actor, Guid tournament, Guid fact, string key, CancellationToken cancellationToken);
}
