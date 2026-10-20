// System tray icon: open settings, pause gestures, install a new version, quit. The hidden files ("Storage") are opened,
// locked and given a new password from it too, but those items show only while ⌥ Option is held as the menu opens: a
// plain click never shows them.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Services;

public sealed class TrayService(UserDataStore store, ILocalizer loc, WindowService windows, UpdateService updates, VaultService vault)
{
    private TrayIcon? _tray;
    private NativeMenuItem? _open, _pause, _quit, _update, _vault, _vaultPassword;

    public void Create(Application app)
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://Filee/Assets/Icons/filee.ico"));
        _open = new NativeMenuItem();
        _open.Click += (_, _) => windows.ShowMain();
        _pause = new NativeMenuItem();
        _pause.Click += (_, _) =>
        {
            store.Settings.Paused = !store.Settings.Paused;
            store.SaveSettings();
        };
        _quit = new NativeMenuItem();
        _quit.Click += (_, _) => (app.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        _update = new NativeMenuItem();
        _update.Click += async (_, _) => await updates.UpdateAsync();
        _vault = new NativeMenuItem();
        _vault.Click += async (_, _) => await ToggleVaultAsync();
        _vaultPassword = new NativeMenuItem();
        _vaultPassword.Click += async (_, _) => await ChangeVaultPasswordAsync();
        vault.StateChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            // Set before the icon is attached: on macOS Avalonia passes the tooltip to native code, which can't take null.
            ToolTipText = "Easy",
            Menu = new NativeMenu { Items = { _open, _pause, new NativeMenuItemSeparator(), _quit } },
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => windows.ShowMain();
        // Asked each time before the menu shows.
        _tray.Menu.NeedsUpdate += (_, _) => _vault.IsVisible = _vaultPassword.IsVisible = vault.Exists && Platform.MacOS.OptionKey.IsDown;
        TrayIcon.SetIcons(app, [_tray]);

        Refresh();
        loc.LanguageChanged += (_, _) => Refresh();
        store.SettingsChanged += (_, _) => Refresh();
        updates.PropertyChanged += (_, e) =>
        {
            if (UpdateTexts.Affects(e.PropertyName))
                Refresh();
        };
    }

    private void Refresh()
    {
        if (_tray?.Menu is not { } menu)
            return;
        // "Update to x.y.z" sits at the top of the menu while a newer release is out ("Downloading… 42%" meanwhile).
        if (updates.LatestVersion is { } latest)
        {
            _update!.Header = updates.Step is UpdateStep.Downloading or UpdateStep.Installing
                ? UpdateTexts.Short(updates, loc)
                : loc.Format("tray.update", latest);
            if (!menu.Items.Contains(_update))
                menu.Items.Insert(0, _update);
        }
        else
        {
            menu.Items.Remove(_update!);
        }
        _open!.Header = loc["tray.open"];
        _pause!.Header = store.Settings.Paused ? loc["tray.resume"] : loc["tray.pause"];
        _quit!.Header = loc["tray.quit"];
        // Hidden files: only once there are some (the first "Hide" makes the vault), and shown only with ⌥ Option.
        if (vault.Exists && !menu.Items.Contains(_vault!))
        {
            _vault!.IsVisible = _vaultPassword!.IsVisible = false;
            menu.Items.Insert(menu.Items.IndexOf(_pause) + 1, _vault!);
            menu.Items.Insert(menu.Items.IndexOf(_vault!) + 1, _vaultPassword!);
        }
        _vault!.Header = vault.IsOpen ? loc["tray.vault_lock"] : loc["tray.vault_open"];
        _vaultPassword!.Header = loc["tray.vault_password"];
        var drag = store.Settings.Triggers.FirstOrDefault(t => t.Enabled && t.Kind == TriggerKind.Drag);
        _tray.ToolTipText = store.Settings.Paused
            ? loc["triggers.paused"]
            : loc.Format("tray.tooltip", drag is null ? "—" : GestureText.Modifiers(drag.Modifiers));
    }

    /// <summary>Asks for the password and shows the hidden files in Finder, or locks them again.</summary>
    private async Task ToggleVaultAsync()
    {
        if (vault.IsOpen)
        {
            if (!await vault.LockAsync())
                await windows.MessageAsync(null, loc["vault.still_in_use"]);
            return;
        }
        while (await windows.AskTextAsync(null, "vault.unlock_question", "password.hint", secret: true) is { } password)
        {
            if (await vault.UnlockAsync(password) is null)
            {
                vault.Reveal();
                return;
            }
        }
    }

    /// <summary>Current password, then the new one twice. A forgotten password can't be changed: nothing opens the vault.</summary>
    private async Task ChangeVaultPasswordAsync()
    {
        while (await windows.AskTextAsync(null, "vault.current_question", "password.hint", secret: true) is { } current)
        {
            string? next;
            do
            {
                next = await windows.AskTextAsync(null, "vault.new_question", "password.hint", secret: true);
                if (next is null)
                    return;
            }
            while (next.Length < 8 || await windows.AskTextAsync(null, "vault.confirm_question", "password.hint", secret: true) is not { } again
                   || again != next);

            var error = await vault.ChangePasswordAsync(current, next);
            await windows.MessageAsync(null, loc[error is null ? "vault.password_changed"
                : error == VaultService.InUse ? "vault.still_in_use"
                : "vault.wrong_password"]);
            if (error is null or VaultService.InUse)
                return;
        }
    }

    public void Dispose() => _tray?.Dispose();
}
