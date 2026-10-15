namespace Calcpad.Core.Security;

/// <summary>
/// Every capability a plugin can request. Calcpad is capability based: a plugin gets nothing
/// by default, and every host API call re-checks the grant at call time.
/// </summary>
public enum PermissionKind
{
    /// <summary>Read objects, strokes and selection from the infinite canvas.</summary>
    CanvasRead,

    /// <summary>Create, modify or delete objects on the infinite canvas.</summary>
    CanvasWrite,

    /// <summary>Read this plugin's own database partition.</summary>
    DatabaseRead,

    /// <summary>Write this plugin's own database partition.</summary>
    DatabaseWrite,

    ClipboardRead,
    ClipboardWrite,
    FileRead,
    FileWrite,

    /// <summary>Open network connections (never implies content upload permission).</summary>
    Network,

    /// <summary>Run inference on a model that lives on this device.</summary>
    AiLocal,

    /// <summary>Send content to a remote AI service. Always additionally gated per request by <see cref="IEgressGuard"/>.</summary>
    AiCloud,

    Camera,
    Microphone,
    Notifications,

    /// <summary>Contribute to the menu bar, shortcuts, panels and the status bar.</summary>
    ShellIntegration
}

public enum PermissionRisk
{
    Low,
    Medium,
    High
}

public enum PermissionState
{
    Unknown,
    Granted,
    Denied,
    Revoked
}

public sealed record PermissionDescriptor(
    PermissionKind Kind,
    string Title,
    string LocalizedTitleZh,
    string Description,
    PermissionRisk Risk);

public static class PermissionCatalog
{
    private static readonly PermissionDescriptor[] Descriptors =
    [
        new(PermissionKind.CanvasRead, "Read canvas", "读取画布",
            "Inspect objects, ink strokes and the current selection on the infinite canvas.", PermissionRisk.Low),
        new(PermissionKind.CanvasWrite, "Modify canvas", "修改画布",
            "Create, move, edit or delete objects on the infinite canvas.", PermissionRisk.Medium),
        new(PermissionKind.DatabaseRead, "Read own data", "读取插件数据",
            "Read the key/value and document records this plugin stored in the local database.", PermissionRisk.Low),
        new(PermissionKind.DatabaseWrite, "Write own data", "写入插件数据",
            "Store records in the local database, inside this plugin's own partition.", PermissionRisk.Low),
        new(PermissionKind.ClipboardRead, "Read clipboard", "读取剪贴板",
            "Read text and images the user copied.", PermissionRisk.Medium),
        new(PermissionKind.ClipboardWrite, "Write clipboard", "写入剪贴板",
            "Copy text, images and math results to the clipboard.", PermissionRisk.Low),
        new(PermissionKind.FileRead, "Read files", "读取文件",
            "Open documents the user explicitly picks.", PermissionRisk.Medium),
        new(PermissionKind.FileWrite, "Write files", "写入文件",
            "Export or save documents to a location the user explicitly picks.", PermissionRisk.Medium),
        new(PermissionKind.Network, "Network access", "网络访问",
            "Open outbound connections. Uploading canvas content is a separate, per request consent.", PermissionRisk.High),
        new(PermissionKind.AiLocal, "Use on-device AI", "使用本地 AI",
            "Run handwriting recognition and question answering with models stored on this device. Nothing leaves the device.", PermissionRisk.Low),
        new(PermissionKind.AiCloud, "Use cloud AI", "使用云端 AI",
            "Ask a remote model. Every payload is shown to you for approval before it is sent, and can be blocked entirely.", PermissionRisk.High),
        new(PermissionKind.Camera, "Use camera", "使用摄像头",
            "Capture a photo onto the canvas.", PermissionRisk.High),
        new(PermissionKind.Microphone, "Use microphone", "使用麦克风",
            "Record audio for dictation.", PermissionRisk.High),
        new(PermissionKind.Notifications, "Show notifications", "显示通知",
            "Post progress and completion notifications.", PermissionRisk.Low),
        new(PermissionKind.ShellIntegration, "Extend the shell", "扩展界面",
            "Insert items into the menu bar, register shortcuts, tools and side panels.", PermissionRisk.Medium)
    ];

    private static readonly Dictionary<PermissionKind, PermissionDescriptor> Map =
        Descriptors.ToDictionary(d => d.Kind);

    public static IReadOnlyList<PermissionDescriptor> All => Descriptors;

    public static PermissionDescriptor Describe(PermissionKind kind) =>
        Map.TryGetValue(kind, out var d) ? d : new PermissionDescriptor(kind, kind.ToString(), kind.ToString(), string.Empty, PermissionRisk.Medium);
}

public sealed record PermissionRequest(
    string PluginId,
    string PluginName,
    IReadOnlyList<PermissionKind> Kinds,
    string Reason,
    bool Remember = true);

public sealed record PermissionDecision(
    bool Granted,
    bool Remembered,
    IReadOnlyList<PermissionKind> GrantedKinds)
{
    public static PermissionDecision Denied { get; } = new(false, false, []);
}

/// <summary>Raised when a plugin calls a host API it was not granted.</summary>
public sealed class PermissionDeniedException : Exception
{
    public PermissionDeniedException(string pluginId, PermissionKind kind, string api)
        : base($"Plugin '{pluginId}' is not authorized for {kind} (while calling {api}).")
    {
        PluginId = pluginId;
        Kind = kind;
        Api = api;
    }

    public string PluginId { get; }

    public PermissionKind Kind { get; }

    public string Api { get; }
}

public sealed class PermissionChangedEventArgs : EventArgs
{
    public PermissionChangedEventArgs(string pluginId, PermissionKind kind, PermissionState state)
    {
        PluginId = pluginId;
        Kind = kind;
        State = state;
    }

    public string PluginId { get; }

    public PermissionKind Kind { get; }

    public PermissionState State { get; }
}
