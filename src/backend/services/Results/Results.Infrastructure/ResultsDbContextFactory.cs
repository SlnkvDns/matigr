using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Matigr.Results.Infrastructure;

public sealed class ResultsDbContextFactory : IDesignTimeDbContextFactory<ResultsDbContext>
{
    public ResultsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Results")
            ?? throw new InvalidOperationException("ConnectionStrings__Results is required for Results migrations.");
        var options = new DbContextOptionsBuilder<ResultsDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new ResultsDbContext(options);
    }
}
