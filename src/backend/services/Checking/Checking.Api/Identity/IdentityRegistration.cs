using Matigr.Checking.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Matigr.Checking.Api.Identity;

public static class IdentityRegistration
{
    public static bool HasDevIdentity(this IHostEnvironment environment) =>
        environment.IsEnvironment("Local") || environment.IsEnvironment("Test");

    public static IServiceCollection AddCheckingIdentity(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton<UserTokenVerifier>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<UserTokenVerifier>((options, verifier) =>
            {
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = verifier.Parameters;
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (UserTokenVerifier.ReadSubject(context.Principal!) is null)
                            context.Fail("Invalid user identity.");
                        return Task.CompletedTask;
                    },
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        context.Response.Headers.WWWAuthenticate = "Bearer";
                        await IdentityHttp.Problem(context.HttpContext, 401, "Unauthorized", "Authentication required")
                            .ExecuteAsync(context.HttpContext);
                    },
                    OnForbidden = context => IdentityHttp.Problem(context.HttpContext, 403, "Forbidden", "Access denied")
                        .ExecuteAsync(context.HttpContext)
                };
            });
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(CheckingAuthorization.ReviewerPolicy, policy => policy.RequireAuthenticatedUser()
                .RequireAssertion(context => UserTokenVerifier.ReadSubject(context.User)?.IsReviewer == true))
            .AddPolicy(CheckingAuthorization.ReviewScopePolicy, policy => policy.RequireAuthenticatedUser()
                .AddRequirements(new ReviewScopeRequirement()));
        services.AddSingleton<IAuthorizationHandler, ReviewScopeHandler>();
        if (environment.HasDevIdentity())
        {
            services.AddSingleton(_ => new DevSubjectDirectory(UserTokenVerifier.Required(configuration, "Fixtures:Directory")));
            services.AddSingleton(provider => new DevSessionIssuer(
                UserTokenVerifier.Required(configuration, "DevIdentity:PrivateKeyPath"),
                UserTokenVerifier.Required(configuration, "Identity:PublicKeyPath"),
                UserTokenVerifier.Required(configuration, "Identity:Issuer"),
                UserTokenVerifier.Required(configuration, "Identity:Audience"),
                TimeSpan.FromSeconds(configuration.GetValue<int?>("DevIdentity:LifetimeSeconds")
                    ?? throw new InvalidOperationException("DevIdentity:LifetimeSeconds is required.")),
                provider.GetRequiredService<TimeProvider>()));
        }
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
