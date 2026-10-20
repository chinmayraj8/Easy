// Hidden files: an encrypted disk image (APFS in an AES-256 sparse bundle, made by macOS's hdiutil) in Easy's data
// folder. Locked, it is one bundle of encrypted bands that nothing reads without the password; "Hide" moves files into
// it, the tray opens it in Finder and locks it again. Easy never stores the password, so a forgotten one can't be
// recovered. Open, it is mounted inside the data folder with -nobrowse: on no Finder sidebar or desktop. Shown in
// Finder, it locks again once no Finder window shows it (checked every 2 s; Finder sends no "window closed").

using System.Diagnostics;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public sealed class VaultService(UserDataStore store, ILogger<VaultService> log) : IDisposable
{
    /// <summary>Locks itself this long after it was opened (when no file in it is still open).</summary>
    private static readonly TimeSpan AutoLock = TimeSpan.FromMinutes(10);

    private Timer? _autoLock;
    private Timer? _finderWatch;
    private bool _seenInFinder;
    private int _watchTicks;

    public string BundlePath => Path.Combine(store.Directory, ".vault.sparsebundle");

    public string MountPoint => Path.Combine(store.Directory, ".vault-open");

    public bool Exists => Directory.Exists(BundlePath);

    /// <summary>Unlocked: its files are reachable at <see cref="MountPoint"/>.</summary>
    public bool IsOpen => File.Exists(Path.Combine(MountPoint, ".easy-vault"));

    public event EventHandler? StateChanged;

    /// <summary>Makes the vault with <paramref name="password"/>. Returns an error message, or null.</summary>
    public async Task<string?> CreateAsync(string password)
    {
        Directory.CreateDirectory(store.Directory);
        var (ok, output) = await HdiutilAsync(password, "create", "-size", "500g", "-type", "SPARSEBUNDLE", "-fs", "APFS",
            "-encryption", "AES-256", "-volname", "Easy Hidden", "-stdinpass", BundlePath);
        if (!ok)
            return output;
        await RunAsync("/usr/bin/chflags", null, "hidden", BundlePath);
        if (await UnlockAsync(password) is { } error)
            return error;
        await File.WriteAllTextAsync(Path.Combine(MountPoint, ".easy-vault"), "");
        return null;
    }

    /// <summary>Opens the vault. Returns an error message (a wrong password), or null.</summary>
    public async Task<string?> UnlockAsync(string password)
    {
        if (IsOpen)
            return null;
        Directory.CreateDirectory(MountPoint);
        var (ok, output) = await HdiutilAsync(password, "attach", BundlePath, "-stdinpass", "-nobrowse", "-noautoopen", "-mountpoint", MountPoint);
        if (!ok)
            return output;
        _autoLock?.Dispose();
        _autoLock = new Timer(_ => _ = LockAsync(), null, AutoLock, AutoLock);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>Closes the vault. False when a file in it is still open (it stays open then).</summary>
    public async Task<bool> LockAsync()
    {
        if (!IsOpen)
            return true;
        var (ok, output) = await HdiutilAsync(null, "detach", MountPoint);
        if (!ok)
        {
            log.LogInformation("The vault stays open: {Output}", output);
            return false;
        }
        _autoLock?.Dispose();
        _autoLock = null;
        _finderWatch?.Dispose();
        _finderWatch = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Moves files and folders into the open vault, noting where each came from (in the vault, so encrypted too).
    /// Returns how many were hidden.
    /// </summary>
    public async Task<int> MoveInAsync(IReadOnlyList<string> paths)
    {
        var origins = ReadOrigins();
        var hidden = 0;
        foreach (var path in paths)
        {
            if (await MoveAsync(path, MountPoint) is not { } moved)
                continue;
            origins[Path.GetFileName(moved)] = Path.GetFullPath(path.TrimEnd('/'));
            hidden++;
        }
        WriteOrigins(origins);
        return hidden;
    }

    /// <summary>True when every path is in the open vault: the donut offers "Unhide" for them.</summary>
    public bool Holds(IReadOnlyList<string> paths) =>
        IsOpen && paths.Count > 0 && paths.All(p => Path.GetDirectoryName(Path.GetFullPath(p.TrimEnd('/'))) == MountPoint);

    /// <summary>
    /// Moves files out of the open vault into the folders they were hidden from (made again when gone; Downloads for
    /// one without a note). Returns how many came back.
    /// </summary>
    public async Task<int> MoveOutAsync(IReadOnlyList<string> paths)
    {
        var origins = ReadOrigins();
        var restored = 0;
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path.TrimEnd('/'));
            var folder = origins.TryGetValue(name, out var origin)
                ? Path.GetDirectoryName(origin)!
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(folder);
            // Back under its own name when that is free ("photo (2).jpg" in the vault was "photo.jpg").
            if (await MoveAsync(path, folder, origin is null ? null : Path.GetFileName(origin)) is null)
                continue;
            origins.Remove(name);
            restored++;
        }
        WriteOrigins(origins);
        return restored;
    }

    /// <summary>
    /// Copies a file or folder into <paramref name="folder"/> (ditto: data, dates, extended attributes) under a free
    /// name, and deletes the original only after its copy is complete. Returns the copy, or null.
    /// </summary>
    private async Task<string?> MoveAsync(string path, string folder, string? name = null)
    {
        name ??= Path.GetFileName(path.TrimEnd('/'));
        var target = Path.Combine(folder, name);
        for (var n = 2; File.Exists(target) || Directory.Exists(target); n++)
            target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");

        if (!await RunAsync("/usr/bin/ditto", null, path, target) || !SameSize(path, target))
        {
            log.LogWarning("Moving {Path} failed; the original stays where it is", path);
            return null;
        }
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else
            File.Delete(path);
        return target;
    }

    private string OriginsPath => Path.Combine(MountPoint, ".easy-origins");

    /// <summary>Name in the vault → full path it was hidden from, one "name TAB path" per line.</summary>
    private Dictionary<string, string> ReadOrigins() =>
        File.Exists(OriginsPath)
            ? File.ReadAllLines(OriginsPath).Select(l => l.Split('\t', 2)).Where(p => p.Length == 2)
                .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.Last()[1])
            : [];

    private void WriteOrigins(Dictionary<string, string> origins) =>
        File.WriteAllLines(OriginsPath, origins.Select(o => $"{o.Key}\t{o.Value}"));

    /// <summary>Shows the open vault in Finder; it locks when its last Finder window closes.</summary>
    public void Reveal()
    {
        Process.Start("/usr/bin/open", [MountPoint])?.Dispose();
        _seenInFinder = false;
        _watchTicks = 0;
        _finderWatch?.Dispose();
        _finderWatch = new Timer(_ => _ = WatchFinderAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private async Task WatchFinderAsync()
    {
        if (!IsOpen)
        {
            _finderWatch?.Dispose();
            return;
        }
        var shown = await ShownInFinderAsync();
        _seenInFinder |= shown;
        // Locks once its window is gone, or when it never showed up (10 s: Finder was slow, or the user closed it at once).
        if (!shown && (_seenInFinder || ++_watchTicks >= 5))
            await LockAsync();
    }

    /// <summary>True while a Finder window shows the vault or a folder in it.</summary>
    private async Task<bool> ShownInFinderAsync()
    {
        const string script = """
            set out to ""
            tell application "Finder"
                repeat with w in Finder windows
                    try
                        set out to out & POSIX path of (target of w as alias) & linefeed
                    end try
                end repeat
            end tell
            return out
            """;
        var output = new System.Text.StringBuilder();
        // A failure (Finder not answering) counts as shown: never lock under an open window by mistake.
        if (!await RunAsync("/usr/bin/osascript", null, ["-e", script], output))
            return true;
        return output.ToString().Split('\n').Any(path => path.TrimEnd('/').StartsWith(MountPoint, StringComparison.Ordinal));
    }

    /// <summary>
    /// Changes the password (the vault is locked first). Returns an error message (the current password is wrong, or
    /// <see cref="InUse"/>: a hidden file is still open), or null.
    /// </summary>
    public async Task<string?> ChangePasswordAsync(string current, string next)
    {
        if (!await LockAsync())
            return InUse;
        var (ok, output) = await HdiutilAsync($"{current}\0{next}\0", "chpass", "-oldstdinpass", "-newstdinpass", BundlePath);
        return ok ? null : output;
    }

    /// <summary>What <see cref="ChangePasswordAsync"/> returns when a hidden file is still open.</summary>
    public const string InUse = "in use";

    /// <summary>Locks a vault left open (Easy quit or crashed while it was open).</summary>
    public void LockLeftOpen()
    {
        if (IsOpen)
            _ = LockAsync();
    }

    public void Dispose()
    {
        _autoLock?.Dispose();
        _finderWatch?.Dispose();
        // Quitting locks it: a file still open in another app keeps it open until that app closes it.
        if (IsOpen)
            HdiutilAsync(null, "detach", MountPoint).GetAwaiter().GetResult();
    }

    private static bool SameSize(string source, string copy) =>
        Directory.Exists(source)
            ? Size(source) == Size(copy)
            : File.Exists(copy) && new FileInfo(source).Length == new FileInfo(copy).Length;

    private static long Size(string folder) =>
        Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : -1;

    private static async Task<(bool Ok, string Output)> HdiutilAsync(string? password, params string[] arguments)
    {
        var output = new System.Text.StringBuilder();
        var ok = await RunAsync("/usr/bin/hdiutil", password, arguments, output);
        return (ok, output.ToString().Trim());
    }

    private static Task<bool> RunAsync(string program, string? stdin, params string[] arguments) =>
        RunAsync(program, stdin, arguments, null);

    /// <summary>Runs a program; <paramref name="stdin"/> (a password) is written to it and never to a command line.</summary>
    private static async Task<bool> RunAsync(string program, string? stdin, string[] arguments, System.Text.StringBuilder? output)
    {
        var info = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        if (stdin is not null)
            await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        output?.Append(await stderr).Append(await stdout);
        return process.ExitCode == 0;
    }
}
