// The hidden-files vault with the real hdiutil (macOS): a file moved in is gone from where it was, the locked vault
// opens only with its password, and the file is there again after unlocking.

using System.Security.Cryptography;
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

    [Fact]
    public async Task Easy_data_folder_and_folders_holding_it_are_never_hidden()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Needs hdiutil.");
            return;
        }
        var folder = Directory.CreateTempSubdirectory("easy-vault").FullName;
        var data = Path.Combine(folder, "data");
        var vault = new VaultService(new UserDataStore(data), NullLogger<VaultService>.Instance);
        try
        {
            Assert.Null(await vault.CreateAsync("correct horse"));
            var settings = Path.Combine(data, "notes.txt");
            await File.WriteAllTextAsync(settings, "x", TestContext.Current.CancellationToken);

            Assert.Equal(0, await vault.MoveInAsync([folder, data, settings]));
            Assert.True(Directory.Exists(vault.BundlePath));
            Assert.True(File.Exists(settings));
        }
        finally
        {
            await vault.LockAsync();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_file_that_cannot_be_removed_stays_where_it_is_and_nothing_crashes()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Needs hdiutil.");
            return;
        }
        var folder = Directory.CreateTempSubdirectory("easy-vault").FullName;
        var locked = Path.Combine(folder, "locked");
        var vault = new VaultService(new UserDataStore(Path.Combine(folder, "data")), NullLogger<VaultService>.Instance);
        try
        {
            Assert.Null(await vault.CreateAsync("correct horse"));
            Directory.CreateDirectory(locked);
            var stuck = Path.Combine(locked, "stuck.txt");
            await File.WriteAllTextAsync(stuck, "stay", TestContext.Current.CancellationToken);
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute); // can't delete from it

            Assert.Equal(0, await vault.MoveInAsync([stuck]));
            Assert.Equal("stay", await File.ReadAllTextAsync(stuck, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(vault.MountPoint, "stuck.txt"))); // no second copy left behind
        }
        finally
        {
            if (Directory.Exists(locked))
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await vault.LockAsync();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Locking_while_files_are_being_hidden_waits_and_never_leaves_one_unencrypted()
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
            Assert.Null(await vault.CreateAsync("correct horse"));
            var files = Enumerable.Range(1, 12).Select(i => Path.Combine(folder, $"doc{i}.bin")).ToList();
            foreach (var file in files)
                await File.WriteAllBytesAsync(file, RandomNumberGenerator.GetBytes(2_000_000), TestContext.Current.CancellationToken);

            // The auto-lock (or the Finder watch) fires while the batch is moving.
            var hiding = vault.MoveInAsync(files);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            var locking = vault.LockAsync();
            Assert.Equal(files.Count, await hiding);
            Assert.True(await locking);

            Assert.False(vault.IsOpen);
            Assert.Empty(Directory.EnumerateFileSystemEntries(vault.MountPoint)); // nothing landed outside the encrypted image
            Assert.All(files, f => Assert.False(File.Exists(f)));
            Assert.Null(await vault.UnlockAsync("correct horse"));
            Assert.All(files, f => Assert.True(File.Exists(Path.Combine(vault.MountPoint, Path.GetFileName(f)))));
        }
        finally
        {
            await vault.LockAsync();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Odd_names_come_back_where_they_were_and_the_notes_never_leave_the_vault()
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
            Assert.Null(await vault.CreateAsync("correct horse"));
            var deep = Directory.CreateDirectory(Path.Combine(folder, "a", "b")).FullName;
            var odd = Path.Combine(deep, "tax\treturn\n2026.pdf");
            await File.WriteAllTextAsync(odd, "numbers", TestContext.Current.CancellationToken);

            Assert.Equal(1, await vault.MoveInAsync([odd]));
            var notes = Directory.EnumerateFiles(vault.MountPoint, ".easy-origins*").ToList();
            Assert.Equal(0, await vault.MoveOutAsync(notes)); // Easy's own notes are not a hidden file
            Assert.True(File.Exists(notes.Single()));

            Assert.Equal(1, await vault.MoveOutAsync([Path.Combine(vault.MountPoint, Path.GetFileName(odd))]));
            Assert.Equal("numbers", await File.ReadAllTextAsync(odd, TestContext.Current.CancellationToken));
        }
        finally
        {
            await vault.LockAsync();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_copy_counts_as_identical_only_byte_for_byte()
    {
        var folder = Directory.CreateTempSubdirectory("easy-identical").FullName;
        try
        {
            string Tree(string name)
            {
                var root = Directory.CreateDirectory(Path.Combine(folder, name)).FullName;
                Directory.CreateDirectory(Path.Combine(root, "sub", "empty"));
                File.WriteAllBytes(Path.Combine(root, "sub", "a.bin"), [1, 2, 3, 4]);
                File.WriteAllText(Path.Combine(root, ".hidden"), "h");
                File.CreateSymbolicLink(Path.Combine(root, "link"), "sub/a.bin");
                return root;
            }
            var source = Tree("source");
            Assert.True(VaultService.Identical(source, Tree("same")));

            var changed = Tree("changed");
            File.WriteAllBytes(Path.Combine(changed, "sub", "a.bin"), [1, 2, 3, 5]); // same size, one byte off
            Assert.False(VaultService.Identical(source, changed));

            var extra = Tree("extra");
            File.WriteAllText(Path.Combine(extra, "more.txt"), "x");
            Assert.False(VaultService.Identical(source, extra));

            var missing = Tree("missing");
            Directory.Delete(Path.Combine(missing, "sub", "empty"));
            Assert.False(VaultService.Identical(source, missing));

            var relinked = Tree("relinked");
            File.Delete(Path.Combine(relinked, "link"));
            File.CreateSymbolicLink(Path.Combine(relinked, "link"), "elsewhere");
            Assert.False(VaultService.Identical(source, relinked));

            Assert.False(VaultService.Identical(Path.Combine(source, "sub", "a.bin"), Path.Combine(folder, "nothing")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Folders_come_back_whole_old_notes_still_work_and_nothing_is_overwritten()
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
            Assert.Null(await vault.CreateAsync("correct horse"));

            // A folder with nested and hidden files goes in and comes back byte for byte.
            var project = Path.Combine(folder, "home", "Project");
            Directory.CreateDirectory(Path.Combine(project, "deep", "er"));
            await File.WriteAllBytesAsync(Path.Combine(project, "deep", "er", "data.bin"), RandomNumberGenerator.GetBytes(300_000), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(project, ".env"), "KEY=1", TestContext.Current.CancellationToken);
            var before = Path.Combine(folder, "before");
            Directory.CreateDirectory(before);
            File.Copy(Path.Combine(project, "deep", "er", "data.bin"), Path.Combine(before, "data.bin"));
            Assert.Equal(1, await vault.MoveInAsync([project]));
            Assert.False(Directory.Exists(project));

            // Meanwhile a new "Project" appeared where it was: it is kept, the hidden one comes back next to it.
            Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(Path.Combine(project, "new.txt"), "new", TestContext.Current.CancellationToken);
            Assert.Equal(1, await vault.MoveOutAsync([Path.Combine(vault.MountPoint, "Project")]));
            Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(project, "new.txt"), TestContext.Current.CancellationToken));
            var back = Path.Combine(folder, "home", "Project (2)");
            Assert.True(VaultService.Identical(Path.Combine(before, "data.bin"), Path.Combine(back, "deep", "er", "data.bin")));
            Assert.Equal("KEY=1", await File.ReadAllTextAsync(Path.Combine(back, ".env"), TestContext.Current.CancellationToken));

            // A vault from before the JSON notes: "name TAB path" lines still send a file home.
            var letter = Path.Combine(folder, "home", "letter.txt");
            await File.WriteAllTextAsync(Path.Combine(vault.MountPoint, "letter.txt"), "dear", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(vault.MountPoint, ".easy-origins"), $"letter.txt\t{letter}\n", TestContext.Current.CancellationToken);
            Assert.Equal(1, await vault.MoveOutAsync([Path.Combine(vault.MountPoint, "letter.txt")]));
            Assert.Equal("dear", await File.ReadAllTextAsync(letter, TestContext.Current.CancellationToken));
        }
        finally
        {
            await vault.LockAsync();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Five_rounds_of_hiding_and_unhiding_bring_every_byte_back()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Needs hdiutil.");
            return;
        }
        var folder = Directory.CreateTempSubdirectory("easy-vault").FullName;
        var vault = new VaultService(new UserDataStore(Path.Combine(folder, "data")), NullLogger<VaultService>.Instance);
        var rng = new Random(7);
        try
        {
            Assert.Null(await vault.CreateAsync("correct horse battery"));
            var home = Path.Combine(folder, "home");
            var items = new List<string>();
            for (var i = 0; i < 8; i++)
            {
                var top = Path.Combine(home, i % 2 == 0 ? $"Folder {i}" : $"dir.{i}", "nested", i % 3 == 0 ? "déjà vu" : "x");
                Directory.CreateDirectory(top);
                for (var j = 0; j < 6; j++)
                    File.WriteAllBytes(Path.Combine(top, j == 0 ? ".secret" : $"file {j}.bin"), RandomNumberGenerator.GetBytes(rng.Next(0, 5_000_000)));
                items.Add(Path.Combine(home, i % 2 == 0 ? $"Folder {i}" : $"dir.{i}"));
            }
            for (var i = 0; i < 10; i++)
            {
                var f = Path.Combine(home, $"loose {i} (copy).dat");
                File.WriteAllBytes(f, RandomNumberGenerator.GetBytes(rng.Next(1, 3_000_000)));
                items.Add(f);
            }
            File.CreateSymbolicLink(Path.Combine(home, "Folder 0", "shortcut"), "nested");
            Directory.CreateDirectory(Path.Combine(home, "Empty"));
            items.Add(Path.Combine(home, "Empty"));
            var reference = Path.Combine(folder, "reference");
            Assert.True(await Ditto(home, reference));

            for (var round = 0; round < 5; round++)
            {
                Assert.Equal(items.Count, await vault.MoveInAsync(items));
                Assert.All(items, p => Assert.False(Path.Exists(p)));
                Assert.True(await vault.LockAsync());
                Assert.Empty(Directory.EnumerateFileSystemEntries(vault.MountPoint));
                Assert.NotNull(await vault.UnlockAsync("wrong"));
                Assert.Null(await vault.UnlockAsync("correct horse battery"));
                var inVault = items.Select(p => Path.Combine(vault.MountPoint, Path.GetFileName(p))).ToList();
                Assert.Equal(items.Count, await vault.MoveOutAsync(inVault));
                Assert.True(VaultService.Identical(reference, home), $"round {round}: not identical");
            }
            Assert.Equal(new[] { ".easy-origins", ".easy-vault" }, Directory.EnumerateFileSystemEntries(vault.MountPoint).Select(Path.GetFileName).Where(n => n != ".fseventsd" && !n!.StartsWith(".Spotlight") && n != ".Trashes").Order());
        }
        finally
        {
            await vault.LockAsync();
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task<bool> Ditto(string a, string b)
    {
        using var p = System.Diagnostics.Process.Start("/usr/bin/ditto", [a, b])!;
        await p.WaitForExitAsync();
        return p.ExitCode == 0;
    }
}
