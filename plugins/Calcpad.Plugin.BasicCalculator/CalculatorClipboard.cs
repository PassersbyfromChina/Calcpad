namespace Calcpad.Plugins.CalculatorKit;

/// <summary>How a copy attempt ended, so the UI can be honest about it.</summary>
public enum ClipboardOutcome
{
    /// <summary>The text reached the system clipboard.</summary>
    Copied,

    /// <summary>Nothing to copy.</summary>
    Empty,

    /// <summary>The host has no clipboard channel at all.</summary>
    Unavailable,

    /// <summary>The user refused the permission, or the platform call failed.</summary>
    Denied
}

/// <summary>
/// Copies text to the system clipboard. The plugin installs the implementation because only the
/// plugin holds the permission gated <c>IPluginContext.Clipboard</c>; widgets never touch the
/// clipboard directly.
/// </summary>
public delegate ValueTask<ClipboardOutcome> ClipboardWriter(string text, CancellationToken ct = default);

/// <summary>Messages a widget shows after a copy attempt; success is only claimed when it happened.</summary>
public static class CalculatorClipboard
{
    /// <summary>User facing message for an outcome, or null when nothing should be shown.</summary>
    public static string? Describe(ClipboardOutcome outcome) => outcome switch
    {
        ClipboardOutcome.Copied => "已复制 / copied",
        ClipboardOutcome.Empty => "没有可复制的内容 / nothing to copy",
        ClipboardOutcome.Denied => "剪贴板未授权 / clipboard permission denied",
        _ => "剪贴板不可用 / clipboard unavailable"
    };
}
