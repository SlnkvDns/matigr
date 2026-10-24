namespace Matigr.Appeals.Application;
public interface IAppealSubmission
{
    Task<AppealReply> SubmitAsync(AppealActor actor, Guid result, long expectedVersion, string key, CancellationToken cancellationToken);
}
