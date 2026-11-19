using System.Security.Cryptography;
using Matigr.Checking.Application;
using Matigr.Checking.Domain;

namespace Matigr.Checking.Infrastructure.Fixtures;

internal sealed class PreparedMaterialStore(PreparedFixtureCatalog catalog) : IReadOnlyMaterialStore
{
    public async Task<Stream> OpenAsync(SolutionReference solution, CancellationToken cancellationToken)
    {
        var prepared = catalog.Solutions.SingleOrDefault(item => Equals(item.Snapshot, solution));
        if (prepared is null) throw new MaterialNotAllowedException();
        try
        {
            // Reject links at every allowlisted component, including after host startup.
            foreach (var component in new[] { catalog.DirectoryPath, Path.Combine(catalog.DirectoryPath, "materials"),
                Path.Combine(catalog.DirectoryPath, prepared.RelativeFile) })
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                    throw new MaterialUnavailableException();
            var bytes = await File.ReadAllBytesAsync(Path.Combine(catalog.DirectoryPath, prepared.RelativeFile), cancellationToken);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != prepared.Sha256)
                throw new MaterialUnavailableException();
            // Never return a file stream that could change after checksum verification.
            return new MemoryStream(bytes, writable: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new MaterialUnavailableException(error);
        }
    }
}
