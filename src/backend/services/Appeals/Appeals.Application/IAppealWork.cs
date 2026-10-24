namespace Matigr.Appeals.Application;
public interface IAppealWork
{
    Task<AppealReply> ListAsync(AppealActor actor, Guid tournament, bool own, CancellationToken cancellationToken);
    Task<AppealReply> GetAsync(AppealActor actor, Guid appeal, CancellationToken cancellationToken);
    Task<AppealReply> ClaimAsync(AppealActor actor, Guid appeal, long expectedVersion, string key, CancellationToken cancellationToken);
}
