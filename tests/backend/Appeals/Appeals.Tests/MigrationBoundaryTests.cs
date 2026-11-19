using Matigr.Appeals.Application;
using Matigr.Appeals.Infrastructure;
using Matigr.Appeals.Infrastructure.Migrations;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Appeals.Tests;

public class MigrationBoundaryTests
{
    [Fact]
    public void Appeals_migration_owns_the_fixture_submission_window()
    {
        var options = new DbContextOptionsBuilder<AppealsDbContext>()
            .UseNpgsql("Host=localhost;Database=appeals_db;Username=appeals_app;Password=unused")
            .Options;
        using var context = new AppealsDbContext(options);

        Assert.Contains("202610230001_InitialSubmissionWindows", context.Database.GetMigrations());
        Assert.Contains("submission_windows", context.Model.GetEntityTypes().Select(entity => entity.GetTableName()));
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.True(new AppealsReadinessState(true).Ready);
        Assert.False(new AppealsReadinessState(false).Ready);
        Assert.IsType<InitialSubmissionWindows>(Activator.CreateInstance(typeof(InitialSubmissionWindows), nonPublic: true));
    }
}
