using Microsoft.EntityFrameworkCore;

namespace Matigr.Results.Infrastructure;

// The context establishes the service-owned migration boundary. Results tables
// are added with RES-BE-02, together with their domain model and seed rules.
public sealed class ResultsDbContext(DbContextOptions<ResultsDbContext> options) : DbContext(options);
