using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Matigr.Checking.Infrastructure.Storage;

public sealed class CheckingDbContextFactory : IDesignTimeDbContextFactory<CheckingDbContext>
{
    public CheckingDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<CheckingDbContext>().UseNpgsql(
            Environment.GetEnvironmentVariable("ConnectionStrings__Checking")
            ?? throw new InvalidOperationException("ConnectionStrings__Checking is required for Checking migrations.")).Options);
}
