using Calcpad.Core.Ai;
using Calcpad.Core.Canvas;
using Calcpad.Core.Data;
using Calcpad.Core.Security;

namespace Calcpad.Core.Plugins;

/// <summary>
/// Everything a plugin is allowed to touch. Each member is a mediated API: the host checks the
/// matching permission on every call, so a plugin cannot widen its own reach at runtime.
/// </summary>
public interface IPluginContext
{
    string PluginId { get; }

    ICanvasApi Canvas { get; }

    IDataStore Data { get; }

    IPermissionBroker Permissions { get; }

    IAiApi Ai { get; }

    IHostApi Host { get; }

    /// <summary>
    /// Clipboard access, gated on <c>ClipboardWrite</c> / <c>ClipboardRead</c> for this plugin.
    /// Defaults to a fail closed implementation so a context without a real clipboard refuses
    /// loudly instead of pretending the copy succeeded.
    /// </summary>
    IClipboardApi Clipboard => DeniedClipboardApi.Instance;

    IPluginSettings Settings { get; }

    IPluginLogger Log { get; }

    /// <summary>Cancelled when the plugin is disabled or the app shuts down.</summary>
    CancellationToken Shutdown { get; }
}

public enum ToastLevel
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record AppInfo(
    string Name,
    string Version,
    string Platform,
    string RuntimeVersion,
    bool SupportsOnDeviceAi,
    string DataDirectory);

/// <summary>UI-agnostic host services. The Avalonia shell implements it.</summary>
public interface IHostApi
{
    AppInfo Info { get; }

    ValueTask ShowToastAsync(string message, ToastLevel level = ToastLevel.Info, CancellationToken ct = default);

    ValueTask<bool> ConfirmAsync(string title, string message, CancellationToken ct = default);

    ValueTask<string?> PromptAsync(string title, string message, string? initial = null, CancellationToken ct = default);

    ValueTask OpenPanelAsync(string pluginId, string panelId, CancellationToken ct = default);

    ValueTask ClosePanelAsync(string pluginId, string panelId, CancellationToken ct = default);

    /// <summary>Moves the camera so that <paramref name="region"/> is comfortably visible.</summary>
    ValueTask RevealRegionAsync(CRect region, CancellationToken ct = default);

    ValueTask DispatchAsync(Action action, CancellationToken ct = default);

    ValueTask<T> DispatchAsync<T>(Func<T> action, CancellationToken ct = default);

    /// <summary>Runs a long operation with a cancellable progress affordance.</summary>
    ValueTask RunWithProgressAsync(string title, Func<IProgressReporter, CancellationToken, ValueTask> work, CancellationToken ct = default);

    /// <summary>
    /// Invokes a command declared by any enabled plugin, including this one. Returns false when no
    /// enabled plugin owns the id. This is how plugins cooperate: the handwriting plugin asks the
    /// math notes plugin to solve a line, and the calculator asks the notes plugin to switch mode.
    /// The target still runs with its own grants, so a plugin cannot use this to borrow privilege.
    /// </summary>
    ValueTask<bool> ExecuteCommandAsync(string commandId, IReadOnlyDictionary<string, string>? arguments = null, CancellationToken ct = default) =>
        ValueTask.FromResult(false);

    /// <summary>
    /// Probes whether a command is currently available without running it, so callers can offer an
    /// honest degraded path instead of guessing. Returns false when no enabled plugin owns the id.
    /// </summary>
    ValueTask<bool> HasCommandAsync(string commandId, CancellationToken ct = default) =>
        ValueTask.FromResult(false);
}

public interface IProgressReporter
{
    void Report(double fraction, string? message = null);
}
