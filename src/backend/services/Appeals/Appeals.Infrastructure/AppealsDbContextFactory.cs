using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Matigr.Appeals.Infrastructure;

public sealed class AppealsDbContextFactory : IDesignTimeDbContextFactory<AppealsDbContext>
{
    public AppealsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Appeals")
            ?? throw new InvalidOperationException("ConnectionStrings__Appeals is required for Appeals migrations.");
        var options = new DbContextOptionsBuilder<AppealsDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new AppealsDbContext(options);
    }
}
