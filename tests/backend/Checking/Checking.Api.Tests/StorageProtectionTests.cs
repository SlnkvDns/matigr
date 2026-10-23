using System.Security.Cryptography;
using Matigr.Checking.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Matigr.Checking.Api.Tests;

public class StorageProtectionTests(IdentityTestFiles files) : IClassFixture<IdentityTestFiles>
{
    [Fact]
    public void Capability_and_sensitive_replay_survive_new_protector_with_same_key()
    {
        var assignment = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var first = new StorageProtection(files.StorageKeyPath);
        var issued = first.IssueCapability(assignment, reviewer);
        Assert.DoesNotContain(issued.Value, issued.Ciphertext);
        Assert.DoesNotContain(issued.Value, issued.ToString());
        var restarted = new StorageProtection(files.StorageKeyPath);
        Assert.Equal(issued.Value, restarted.RestoreCapability(assignment, reviewer, issued.Hash, issued.Ciphertext));
        Assert.True(StorageProtection.MatchesHash(issued.Value, issued.Hash));
        Assert.False(StorageProtection.MatchesHash("wrong", issued.Hash));
        var response = "{\"capability\":\"" + issued.Value + "\"}";
        var encrypted = first.ProtectHttpResponse(reviewer, "TakeNext", "retry-key", response);
        Assert.DoesNotContain(issued.Value, encrypted);
        Assert.Equal(response, restarted.RestoreHttpResponse(reviewer, "TakeNext", "retry-key", encrypted));
    }

    [Fact]
    public void Ciphertext_is_bound_to_record_subject_purpose_and_integrity()
    {
        var protector = new StorageProtection(files.StorageKeyPath);
        var assignment = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var issued = protector.IssueCapability(assignment, reviewer);
        Assert.ThrowsAny<CryptographicException>(() => protector.RestoreCapability(Guid.NewGuid(), reviewer, issued.Hash, issued.Ciphertext));
        Assert.ThrowsAny<CryptographicException>(() => protector.RestoreCapability(assignment, Guid.NewGuid(), issued.Hash, issued.Ciphertext));
        Assert.Throws<CryptographicException>(() => protector.RestoreCapability(assignment, reviewer, new string('0', 64), issued.Ciphertext));
        var bytes = Convert.FromBase64String(issued.Ciphertext);
        bytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => protector.RestoreCapability(assignment, reviewer, issued.Hash, Convert.ToBase64String(bytes)));
        Assert.ThrowsAny<CryptographicException>(() => protector.RestoreHttpResponse(reviewer, "TakeNext", "key", issued.Ciphertext));
        var replay = protector.ProtectHttpResponse(reviewer, "TakeNext", "key", "response");
        Assert.ThrowsAny<CryptographicException>(() => protector.RestoreHttpResponse(reviewer, "Complete", "key", replay));
        Assert.ThrowsAny<CryptographicException>(() => protector.RestoreHttpResponse(reviewer, "TakeNext", "other", replay));
    }

    [Fact]
    public void Missing_or_invalid_key_fails_without_silent_regeneration()
    {
        var path = Path.Combine(Path.GetTempPath(), "matigr-missing-" + Guid.NewGuid());
        Assert.Throws<FileNotFoundException>(() => new StorageProtection(path));
        File.WriteAllText(path, "invalid");
        try { Assert.Throws<InvalidOperationException>(() => new StorageProtection(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Checking_model_and_migration_stay_in_sync()
    {
        using var db = new CheckingDbContext(new DbContextOptionsBuilder<CheckingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        Assert.Single(db.Database.GetMigrations());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(10, db.Model.GetEntityTypes().Count());
        var assignment = db.Model.FindEntityType(typeof(AssignmentRow))!;
        Assert.Equal(2, assignment.GetIndexes().Count(index => index.IsUnique && index.GetFilter() == "state = 'Active'"));
        Assert.True(db.Model.FindEntityType(typeof(CheckRow))!.FindProperty(nameof(CheckRow.Version))!.IsConcurrencyToken);
    }
}
