using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;

namespace Calcpad.App.Services;

/// <summary>Raw clipboard access owned by the shell. Plugins never see this type.</summary>
public interface IShellClipboard
{
    ValueTask SetTextAsync(string text, CancellationToken ct = default);

    ValueTask<string?> GetTextAsync(CancellationToken ct = default);
}

/// <summary>
/// The gated facade handed to one plugin. Every call re-checks the grant, and the first call from
/// a plugin that has not been granted yet asks the user, so the permission is real rather than
/// declarative.
/// </summary>
public sealed class GatedClipboard : IClipboardApi
{
    private readonly IPermissionBroker _permissions;
    private readonly string _pluginId;
    private readonly IShellClipboard _shell;

    public GatedClipboard(IPermissionBroker permissions, string pluginId, IShellClipboard shell)
    {
        _permissions = permissions;
        _pluginId = pluginId;
        _shell = shell;
    }

    public async ValueTask SetTextAsync(string text, CancellationToken ct = default)
    {
        await EnsureAsync(PermissionKind.ClipboardWrite, "把内容复制到系统剪贴板", nameof(SetTextAsync)).ConfigureAwait(false);
        await _shell.SetTextAsync(text, ct).ConfigureAwait(false);
    }

    public async ValueTask<string?> GetTextAsync(CancellationToken ct = default)
    {
        await EnsureAsync(PermissionKind.ClipboardRead, "读取系统剪贴板内容", nameof(GetTextAsync)).ConfigureAwait(false);
        return await _shell.GetTextAsync(ct).ConfigureAwait(false);
    }

    private async ValueTask EnsureAsync(PermissionKind kind, string reason, string api)
    {
        if (_permissions.IsGranted(_pluginId, kind))
        {
            return;
        }

        var decision = await _permissions.RequestAsync(new PermissionRequest(_pluginId, _pluginId, [kind], reason)).ConfigureAwait(false);
        if (!decision.Granted)
        {
            throw new PermissionDeniedException(_pluginId, kind, api);
        }
    }
}

/// <summary>Avalonia backed clipboard. Always runs the platform call on the UI thread.</summary>
public sealed class AvaloniaShellClipboard : IShellClipboard
{
    private readonly Func<TopLevel?> _topLevelProvider;

    public AvaloniaShellClipboard(Func<TopLevel?> topLevelProvider) => _topLevelProvider = topLevelProvider;

    public async ValueTask SetTextAsync(string text, CancellationToken ct = default)
    {
        var clipboard = await Dispatcher.UIThread.InvokeAsync(() => _topLevelProvider()?.Clipboard).GetTask().ConfigureAwait(false);
        if (clipboard is null)
        {
            throw new InvalidOperationException("No clipboard is available on this platform yet.");
        }

        await clipboard.SetTextAsync(text ?? string.Empty).ConfigureAwait(false);
    }

    public async ValueTask<string?> GetTextAsync(CancellationToken ct = default)
    {
        var clipboard = await Dispatcher.UIThread.InvokeAsync(() => _topLevelProvider()?.Clipboard).GetTask().ConfigureAwait(false);
        if (clipboard is null)
        {
            return null;
        }

        // TryGetTextAsync returns null instead of throwing when the clipboard holds no text.
        return await clipboard.TryGetTextAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the top level for both lifetimes: the desktop main window, or the single view used
    /// on Android.
    /// </summary>
    public static TopLevel? ResolveTopLevel()
    {
        switch (Application.Current?.ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                return desktop.MainWindow;
            case ISingleViewApplicationLifetime single:
                return single.MainView is null ? null : TopLevel.GetTopLevel(single.MainView);
            default:
                return null;
        }
    }
}
