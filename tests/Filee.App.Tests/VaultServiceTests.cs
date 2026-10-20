// The hidden-files vault with the real hdiutil (macOS): a file moved in is gone from where it was, the locked vault
// opens only with its password, and the file is there again after unlocking.

using Filee.App.Services;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public class VaultServiceTests
{
    [Fact]
    public async Task Hidden_files_come_back_only_with_the_password()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Needs hdiutil.");
            return;
        }
        var folder = Directory.CreateTempSubdirectory("easy-vault").FullName;
        var vault = new VaultService(new UserDataStore(Path.Combine(folder, "data")), NullLogger<VaultService>.Instance);
        try
        {
            var secret = Path.Combine(folder, "diary.txt");
            await File.WriteAllTextAsync(secret, "dear diary", TestContext.Current.CancellationToken);

            Assert.Null(await vault.CreateAsync("correct horse"));
            Assert.Equal(1, await vault.MoveInAsync([secret]));
            Assert.False(File.Exists(secret));
            Assert.True(await vault.LockAsync());
            Assert.False(vault.IsOpen);
            Assert.False(File.Exists(Path.Combine(vault.MountPoint, "diary.txt")));

            Assert.NotNull(await vault.UnlockAsync("wrong horse"));
            Assert.False(vault.IsOpen);
            Assert.Null(await vault.UnlockAsync("correct horse"));
            Assert.Equal("dear diary", await File.ReadAllTextAsync(Path.Combine(vault.MountPoint, "diary.txt"), TestContext.Current.CancellationToken));

            // A new password: the old one no longer opens it, the files are still there.
            Assert.NotNull(await vault.ChangePasswordAsync("wrong horse", "battery staple"));
            Assert.Null(await vault.ChangePasswordAsync("correct horse", "battery staple"));
            Assert.False(vault.IsOpen);
            Assert.NotNull(await vault.UnlockAsync("correct horse"));
            Assert.Null(await vault.UnlockAsync("battery staple"));
            Assert.True(File.Exists(Path.Combine(vault.MountPoint, "diary.txt")));

            // Unhide: back where it was hidden from, also when that folder is gone meanwhile.
            Directory.CreateDirectory(Path.Combine(folder, "notes"));
            var nested = Path.Combine(folder, "notes", "plan.txt");
            await File.WriteAllTextAsync(nested, "the plan", TestContext.Current.CancellationToken);
            Assert.Equal(1, await vault.MoveInAsync([nested]));
            Directory.Delete(Path.Combine(folder, "notes"));
            var inVault = new[] { Path.Combine(vault.MountPoint, "diary.txt"), Path.Combine(vault.MountPoint, "plan.txt") };
            Assert.True(vault.Holds(inVault));
            Assert.False(vault.Holds([secret]));
            Assert.Equal(2, await vault.MoveOutAsync(inVault));
            Assert.Equal("dear diary", await File.ReadAllTextAsync(secret, TestContext.Current.CancellationToken));
            Assert.Equal("the plan", await File.ReadAllTextAsync(nested, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(inVault[0]));
        }
        finally
        {
            await vault.LockAsync();
            Directory.Delete(folder, recursive: true);
        }
    }
}
