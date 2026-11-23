using Matigr.Appeals.Application;
using Microsoft.Extensions.DependencyInjection;
namespace Matigr.Appeals.Infrastructure;
public static class SubmissionRegistration
{
    public static IServiceCollection AddAppealSubmissions(this IServiceCollection services) => services.AddScoped<IAppealSubmission, AppealSubmission>();
}
