using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Matigr.Checking.TestSupport;

public sealed class PreparedFixtureFiles : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "matigr-prepared-" + Guid.NewGuid());
    public PreparedFixtureFiles()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "infra/fixtures/manifest.json"))) root = root.Parent;
        var source = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Fixtures not found."), "infra/fixtures");
        Directory.CreateDirectory(Path.Combine(DirectoryPath, "materials"));
        foreach (var file in Directory.GetFiles(Path.Combine(source, "materials")))
            File.Copy(file, Path.Combine(DirectoryPath, "materials", Path.GetFileName(file)));
        File.Copy(Path.Combine(source, "manifest.json"), Path.Combine(DirectoryPath, "manifest.json"));
        File.Copy(Path.Combine(source, "manifest.sha256"), Path.Combine(DirectoryPath, "manifest.sha256"));
    }

    public void Rewrite(Action<JsonObject> change)
    {
        var path = Path.Combine(DirectoryPath, "manifest.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(root);
        File.WriteAllText(path, root.ToJsonString());
        File.WriteAllText(Path.Combine(DirectoryPath, "manifest.sha256"), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }

    public void Dispose()
    {
        var root = Path.GetFullPath(DirectoryPath);
        if (!root.StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "matigr-prepared-"), StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected temporary fixture path.");
        // Delete only this helper's direct files/directories; never recurse through links.
        foreach (var file in Directory.GetFiles(Path.Combine(root, "materials"))) File.Delete(file);
        Directory.Delete(Path.Combine(root, "materials"));
        foreach (var file in Directory.GetFiles(root)) File.Delete(file);
        Directory.Delete(root);
    }
}
