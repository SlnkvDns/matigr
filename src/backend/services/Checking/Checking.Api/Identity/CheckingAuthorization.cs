using Matigr.Checking.Application.Identity;
using Microsoft.AspNetCore.Authorization;

namespace Matigr.Checking.Api.Identity;

public sealed record ReviewScope(string TournamentId, bool SeniorQueue = false);
public sealed record ReviewScopeRequirement : IAuthorizationRequirement;

public sealed class ReviewScopeHandler : AuthorizationHandler<ReviewScopeRequirement, ReviewScope>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
        ReviewScopeRequirement requirement, ReviewScope resource)
    {
        if (UserTokenVerifier.ReadSubject(context.User)?.CanReview(resource.TournamentId, resource.SeniorQueue) == true)
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public static class CheckingAuthorization
{
    public const string ReviewerPolicy = "CheckingReviewer";
    public const string ReviewScopePolicy = "CheckingReviewScope";

    // Commands obtain the actor here, never from browser IDs or role/team headers.
    public static AuthenticatedSubject CurrentSubject(this HttpContext context) =>
        UserTokenVerifier.ReadSubject(context.User)
            ?? throw new InvalidOperationException("An authenticated user is required.");

    public static RouteGroupBuilder MapCheckingApi(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGroup("/api/v1/checking").RequireAuthorization(ReviewerPolicy);
}
