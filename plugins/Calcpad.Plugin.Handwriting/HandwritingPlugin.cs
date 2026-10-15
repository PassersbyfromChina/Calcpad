using Calcpad.Core.Ai;
using Calcpad.Core.Canvas;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;
using Calcpad.Plugin.Handwriting.Ui;
using Calcpad.Plugin.Sdk.Ui;
using PluginCommandContext = Calcpad.Core.Plugins.CommandContext;

namespace Calcpad.Plugin.Handwriting;

/// <summary>
/// Handwriting to text. The plugin owns the whole user experience: scope selection (whole canvas or
/// lasso), the candidate picker that appears when the recogniser refuses to guess, per object editing,
/// write back with undo, and the settings panel that lists the local models it found.
/// <para>
/// Everything the plugin does goes through <see cref="IPluginContext"/>: the canvas facade is
/// permission checked for this plugin id and <see cref="IAiApi"/> is the per plugin AI facade, so a
/// cloud upload always ends up in the host's egress review dialog.
/// </para>
/// </summary>
public sealed class HandwritingPlugin : IPlugin
{
    private readonly HashSet<PermissionKind> _granted = [];
    private IPluginContext? _context;

    /// <inheritdoc />
    public PluginManifest Manifest { get; } = new()
    {
        Id = "calcpad.handwriting",
        Name = "手写转文字",
        Version = "0.0.0",
        Description = "把画布上的手写笔迹识别成文字或数学表达式。默认完全离线；开启云端后每次上传都会先给你看要发送的内容。",
        Author = "PassersbyfromChina",
        Keywords = ["handwriting", "ink", "ocr", "手写", "识别", "转文字"],
        Permissions =
        [
            PermissionKind.CanvasRead,
            PermissionKind.CanvasWrite,
            PermissionKind.ShellIntegration,
            PermissionKind.AiLocal,
            PermissionKind.AiCloud
        ],
        Menus =
        [
            new MenuContribution("AI/手写转文字", "整画布转文字", HandwritingCommands.RecognizeWholeCanvas,
                Shortcut: "Ctrl+Shift+H", Order: 10, LocalizedTitleZh: "整画布转文字"),
            new MenuContribution("AI/手写转文字", "圈选部分转文字", HandwritingCommands.RecognizeRegion,
                Shortcut: "Ctrl+Shift+L", Order: 20, LocalizedTitleZh: "圈选部分转文字"),
            new MenuContribution("AI/手写转文字", "重新识别当前对象", HandwritingCommands.ReRecognizeObject,
                Order: 30, RequiresSelection: true, LocalizedTitleZh: "重新识别当前对象"),
            new MenuContribution("AI/手写转文字", "识别结果…", HandwritingCommands.OpenResults, Order: 80, LocalizedTitleZh: "识别结果…"),
            new MenuContribution("AI/手写转文字", "识别设置…", HandwritingCommands.OpenSettings, Order: 90, LocalizedTitleZh: "识别设置…")
        ],
        Shortcuts =
        [
            new ShortcutContribution("Ctrl+Shift+H", HandwritingCommands.RecognizeWholeCanvas, "整画布转文字", LocalizedTitleZh: "整画布转文字"),
            new ShortcutContribution("Ctrl+Shift+L", HandwritingCommands.RecognizeRegion, "圈选部分转文字", LocalizedTitleZh: "圈选部分转文字")
        ],
        Panels =
        [
            new PanelContribution(HandwritingCommands.ResultsPanelId, "手写识别", HandwritingCommands.OpenResults,
                PanelPlacement.Right, Order: 10, PreferredWidth: 400, LocalizedTitleZh: "手写识别"),
            new PanelContribution(HandwritingCommands.SettingsPanelId, "手写识别设置", HandwritingCommands.OpenSettings,
                PanelPlacement.Right, Order: 90, PreferredWidth: 420, LocalizedTitleZh: "手写识别设置")
        ],
        Tools =
        [
            new ToolContribution("handwriting.lasso", "圈选识别", HandwritingCommands.RecognizeRegion,
                Shortcut: "L", Order: 20, LocalizedTitleZh: "圈选识别")
        ],
        EnabledByDefault = true
    };

    /// <summary>Current settings; the settings panel updates them.</summary>
    public HandwritingSettings Settings { get; private set; } = new();

    /// <summary>The context the shell handed over, or null after shutdown. Panels use it to close overlays.</summary>
    public IPluginContext? Context => _context;

    /// <summary>Last recognition pass, or null when nothing was recognised yet.</summary>
    public RecognitionSession? Session { get; private set; }

    /// <summary>Raised whenever <see cref="Session"/> or <see cref="Settings"/> change.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Theme reported by the shell, used by the panels.</summary>
    public PluginTheme Theme { get; private set; } = PluginTheme.IpadDark;

    /// <inheritdoc />
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Settings = HandwritingSettings.Load(context.Settings);

        if (context is IPluginUiContext ui)
        {
            Theme = ui.Ui.Theme;
            ui.Ui.RegisterPanel(HandwritingCommands.ResultsPanelId, () => new HandwritingPanel(this).Build());
            ui.Ui.RegisterPanel(HandwritingCommands.SettingsPanelId, () => new HandwritingSettingsPanel(this).Build());
            ui.Ui.RegisterOverlay(HandwritingCommands.ScopeOverlayId, () => new ScopePicker(this).Build());
        }
        else
        {
            context.Log.Info("Handwriting plugin started without a UI registry; commands remain available.");
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ShutdownAsync(CancellationToken ct = default)
    {
        _context = null;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask ExecuteCommandAsync(PluginCommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var plugin = _context;
        if (plugin is null)
        {
            return;
        }

        switch (context.CommandId)
        {
            case HandwritingCommands.RecognizeWholeCanvas:
                await RecognizeWholeCanvasAsync(ct).ConfigureAwait(false);
                break;

            case HandwritingCommands.RecognizeRegion:
                await RecognizeRegionAsync(ct).ConfigureAwait(false);
                break;

            case HandwritingCommands.RecognizeSelection:
                await RecognizeSelectionAsync(ct).ConfigureAwait(false);
                break;

            case HandwritingCommands.ReRecognizeObject:
                await ReRecognizeObjectAsync(context.SourceObjectId, ct).ConfigureAwait(false);
                break;

            case HandwritingCommands.AcceptResult:
                await AcceptAsync(ct).ConfigureAwait(false);
                break;

            case HandwritingCommands.UndoResult:
                await UndoAsync(ct).ConfigureAwait(false);
                break;

            case HandwritingCommands.OpenResults:
                await plugin.Host.OpenPanelAsync(plugin.PluginId, HandwritingCommands.ResultsPanelId, ct).ConfigureAwait(false);
                break;

            case HandwritingCommands.OpenSettings:
                await plugin.Host.OpenPanelAsync(plugin.PluginId, HandwritingCommands.SettingsPanelId, ct).ConfigureAwait(false);
                break;

            default:
                plugin.Log.Debug($"Handwriting plugin ignored unknown command '{context.CommandId}'.");
                break;
        }
    }

    /// <summary>Recognises every ink object on the canvas (the cloud path crops per object).</summary>
    public async ValueTask RecognizeWholeCanvasAsync(CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null || !await PrepareAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var objects = await plugin.Canvas.QueryAsync(new CanvasQuery { Kinds = [ObjectKinds.Ink] }, ct).ConfigureAwait(false);
        if (objects.Count == 0)
        {
            await plugin.Host.ShowToastAsync("画布上没有笔迹", ToastLevel.Info, ct).ConfigureAwait(false);
            return;
        }

        var confirmed = await plugin.Host.ConfirmAsync(
            "整画布转文字",
            $"将处理 {objects.Count} 个笔迹对象。云端模式会按对象逐个裁剪并逐个征求你的同意，不会上传整画布图片。",
            ct).ConfigureAwait(false);
        if (!confirmed)
        {
            return;
        }

        await RunAsync(RecognitionScope.WholeCanvas, null, null, ct).ConfigureAwait(false);
    }

    /// <summary>Lasso a region with the contributed tool and recognise only that region.</summary>
    public async ValueTask RecognizeRegionAsync(CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null || !await PrepareAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var activated = await plugin.Canvas.ActivateToolAsync("handwriting.lasso", ct).ConfigureAwait(false);
        var lasso = await FindLassoAsync(ct).ConfigureAwait(false);

        if (lasso is null)
        {
            if (!activated)
            {
                var useSelection = await plugin.Host.ConfirmAsync(
                    "圈选部分转文字",
                    "当前界面没有可用的套索工具。改为识别当前选中的对象吗？",
                    ct).ConfigureAwait(false);
                if (useSelection)
                {
                    await RecognizeSelectionAsync(ct).ConfigureAwait(false);
                }

                return;
            }

            await plugin.Host.ShowToastAsync("请在画布上圈出要识别的内容，然后再次执行「圈选部分转文字」", ToastLevel.Info, ct).ConfigureAwait(false);
            return;
        }

        var polygon = lasso.Points.ToList();
        var ids = lasso.CapturedObjectIds.Count > 0
            ? lasso.CapturedObjectIds.ToList()
            : await ObjectsInsideAsync(polygon, ct).ConfigureAwait(false);

        if (ids.Count == 0)
        {
            await plugin.Host.ShowToastAsync("圈选范围内没有可识别的对象", ToastLevel.Warning, ct).ConfigureAwait(false);
        }

        // The lasso is a temporary selection artefact: remove it so it does not pollute the canvas.
        await plugin.Canvas.RemoveAsync(lasso.Id, ct).ConfigureAwait(false);

        var area = CRect.FromPoints(polygon);
        var confirmed = await plugin.Host.ConfirmAsync(
            "圈选部分转文字",
            $"将处理圈选范围内的 {ids.Count} 个对象（区域 {area.Width:0}×{area.Height:0} 世界单位）。",
            ct).ConfigureAwait(false);
        if (!confirmed)
        {
            return;
        }

        await RunAsync(RecognitionScope.Region, ids, polygon, ct).ConfigureAwait(false);
    }

    /// <summary>Recognises the current selection.</summary>
    public async ValueTask RecognizeSelectionAsync(CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null || !await PrepareAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var selection = plugin.Canvas.Selection.ToList();
        if (selection.Count == 0)
        {
            await plugin.Host.ShowToastAsync("没有选中任何对象", ToastLevel.Warning, ct).ConfigureAwait(false);
            return;
        }

        await RunAsync(RecognitionScope.Selection, selection, null, ct).ConfigureAwait(false);
    }

    /// <summary>Recognises one object again (menu entry on an object).</summary>
    public async ValueTask ReRecognizeObjectAsync(string? objectId, CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null || !await PrepareAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var id = objectId ?? plugin.Canvas.Selection.FirstOrDefault();
        if (string.IsNullOrEmpty(id))
        {
            await plugin.Host.ShowToastAsync("请先选中一个笔迹对象", ToastLevel.Warning, ct).ConfigureAwait(false);
            return;
        }

        await RunAsync(RecognitionScope.Object, [id], null, ct).ConfigureAwait(false);
    }

    /// <summary>Runs one recognition pass, then either applies it or asks the user.</summary>
    public async ValueTask RunAsync(
        RecognitionScope scope,
        IReadOnlyList<string>? objectIds,
        IReadOnlyList<CPoint>? region,
        CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null)
        {
            return;
        }

        var allowCloud = Settings.Engine == HandwritingEnginePreference.AllowCloud;
        if (allowCloud && !await EnsureAsync(PermissionKind.AiCloud, "开启后会使用云端识别；每次上传前仍会显示完整内容并征求你的同意。", ct).ConfigureAwait(false))
        {
            await plugin.Host.ShowToastAsync("未获得云端 AI 授权，本次使用本地识别", ToastLevel.Warning, ct).ConfigureAwait(false);
        }

        var session = new RecognitionSession(plugin);
        RecognitionResult? result = null;
        await plugin.Host.RunWithProgressAsync(
            "识别手写…",
            async (progress, token) =>
            {
                progress.Report(0.1, "读取笔迹");
                result = await session
                    .RunAsync(scope, Settings, objectIds, region, null, token)
                    .ConfigureAwait(false);
                progress.Report(1.0, "完成");
            },
            ct).ConfigureAwait(false);

        Session = session;
        StateChanged?.Invoke(this, EventArgs.Empty);

        if (result is null)
        {
            await plugin.Host.ShowToastAsync("识别没有返回结果", ToastLevel.Warning, ct).ConfigureAwait(false);
            return;
        }

        var summary = $"置信度 {result.Confidence:P0}（{DescribeTier(result.Tier)}），引擎 {result.EngineName}";
        plugin.Log.Info($"Handwriting recognition: scope={scope} text=\"{result.Text}\" {summary} {result.Diagnostics}");

        if (result.NeedsUserChoice)
        {
            // Never guess: show the candidates and let the user decide.
            await plugin.Host.ShowToastAsync($"置信度不足，请从候选中选择：{summary}", ToastLevel.Warning, ct).ConfigureAwait(false);
            await plugin.Host.OpenPanelAsync(plugin.PluginId, HandwritingCommands.ResultsPanelId, ct).ConfigureAwait(false);
            return;
        }

        if (await EnsureAsync(PermissionKind.CanvasWrite, "把手写识别结果写回画布需要修改画布。", ct).ConfigureAwait(false))
        {
            await session.AcceptAsync(Settings, ct).ConfigureAwait(false);
            StateChanged?.Invoke(this, EventArgs.Empty);
            var note = result.Corrections.Count > 0 ? $"，已纠错 {result.Corrections.Count} 处" : string.Empty;
            await plugin.Host.ShowToastAsync($"已写入：{session.TopText}{note}（{summary}）", ToastLevel.Success, ct).ConfigureAwait(false);
        }

        if (result.Tier != RecognitionConfidenceTier.High)
        {
            // Medium confidence is applied but must be reviewable.
            await plugin.Host.OpenPanelAsync(plugin.PluginId, HandwritingCommands.ResultsPanelId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Accepts the pending session (called by the results panel and by the menu).</summary>
    public async ValueTask AcceptAsync(CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null || Session is null)
        {
            return;
        }

        if (!await EnsureAsync(PermissionKind.CanvasWrite, "把手写识别结果写回画布需要修改画布。", ct).ConfigureAwait(false))
        {
            return;
        }

        await Session.AcceptAsync(Settings, ct).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
        await plugin.Host.ShowToastAsync($"已写回 {Session.Writebacks.Count(w => w.Selected)} 个对象", ToastLevel.Success, ct).ConfigureAwait(false);
    }

    /// <summary>Undoes the last write back.</summary>
    public async ValueTask UndoAsync(CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null || Session is null)
        {
            return;
        }

        var undone = await Session.UndoAsync(ct).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
        await plugin.Host.ShowToastAsync(undone ? "已撤销上一次写回" : "没有可撤销的写回", undone ? ToastLevel.Success : ToastLevel.Info, ct).ConfigureAwait(false);
    }

    /// <summary>Persists new settings from the settings panel.</summary>
    public async ValueTask UpdateSettingsAsync(HandwritingSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        if (_context is { } plugin)
        {
            await Settings.SaveAsync(plugin.Settings, ct).ConfigureAwait(false);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs the AI capability probe used by the settings panel.</summary>
    public async ValueTask<IReadOnlyList<LocalModelInfo>> RefreshLocalModelsAsync(CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null)
        {
            return [];
        }

        await Task.Yield();
        return plugin.Ai.DiscoverLocalModels();
    }

    /// <summary>Recognition capabilities as reported by the host, for the settings panel.</summary>
    public AiCapabilities? Capabilities => _context?.Ai.Capabilities;

    /// <summary>Describes a scope before it runs, so the UI can show object counts up front.</summary>
    public async ValueTask<string> DescribeScopeAsync(
        RecognitionScope scope,
        IReadOnlyList<string>? objectIds = null,
        IReadOnlyList<CPoint>? region = null,
        CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null)
        {
            return string.Empty;
        }

        var count = scope switch
        {
            RecognitionScope.WholeCanvas => (await plugin.Canvas.QueryAsync(new CanvasQuery { Kinds = [ObjectKinds.Ink] }, ct).ConfigureAwait(false)).Count,
            RecognitionScope.Selection => plugin.Canvas.Selection.Count,
            RecognitionScope.Region => objectIds?.Count ?? (region is null ? 0 : (await ObjectsInsideAsync(region, ct).ConfigureAwait(false)).Count),
            _ => objectIds?.Count ?? (plugin.Canvas.Selection.Count > 0 ? 1 : 0)
        };

        var area = region is { Count: > 0 } ? CRect.FromPoints(region) : CRect.Empty;
        var areaText = area.IsEmpty ? string.Empty : $"，区域 {area.Width:0}×{area.Height:0}";
        var uploadText = Settings.Engine == HandwritingEnginePreference.AllowCloud
            ? "云端开启时按对象逐个上传并逐个确认"
            : "仅本地识别，不上传";
        return $"{count} 个对象{areaText}，{uploadText}";
    }

    private async ValueTask<bool> PrepareAsync(CancellationToken ct)
    {
        if (!await EnsureAsync(PermissionKind.CanvasRead, "识别手写需要读取画布上的笔迹。", ct).ConfigureAwait(false))
        {
            return false;
        }

        return await EnsureAsync(PermissionKind.AiLocal, "手写识别默认在本机完成，不会联网。", ct).ConfigureAwait(false);
    }

    private async ValueTask<bool> EnsureAsync(PermissionKind kind, string reason, CancellationToken ct)
    {
        var plugin = _context;
        if (plugin is null)
        {
            return false;
        }

        if (_granted.Contains(kind) || plugin.Permissions.IsGranted(plugin.PluginId, kind))
        {
            _granted.Add(kind);
            return true;
        }

        var decision = await plugin.Permissions.RequestAsync(
            new PermissionRequest(plugin.PluginId, Manifest.Name, [kind], reason),
            ct).ConfigureAwait(false);

        if (decision.Granted)
        {
            _granted.Add(kind);
            return true;
        }

        await plugin.Host.ShowToastAsync($"未授予「{PermissionCatalog.Describe(kind).Title}」权限", ToastLevel.Warning, ct).ConfigureAwait(false);
        return false;
    }

    private async ValueTask<ShapeObject?> FindLassoAsync(CancellationToken ct)
    {
        var plugin = _context;
        if (plugin is null)
        {
            return null;
        }

        var shapes = await plugin.Canvas
            .QueryAsync(new CanvasQuery { Kinds = [ObjectKinds.Shape] }, ct)
            .ConfigureAwait(false);

        return shapes
            .OfType<ShapeObject>()
            .Where(s => s.Points.Count >= 3)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefault();
    }

    private async ValueTask<IReadOnlyList<string>> ObjectsInsideAsync(IReadOnlyList<CPoint> polygon, CancellationToken ct)
    {
        var plugin = _context;
        if (plugin is null || polygon.Count < 3)
        {
            return [];
        }

        var bounds = CRect.FromPoints(polygon);
        var objects = await plugin.Canvas.QueryAsync(new CanvasQuery { Region = bounds }, ct).ConfigureAwait(false);
        return [.. objects.Where(o => o.Kind == ObjectKinds.Ink && ContainsPoint(polygon, o.Bounds.Center)).Select(o => o.Id)];
    }

    private static bool ContainsPoint(IReadOnlyList<CPoint> polygon, CPoint point)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            var denominator = pj.Y - pi.Y;
            if (Math.Abs(denominator) < double.Epsilon)
            {
                continue;
            }

            if ((pi.Y > point.Y) != (pj.Y > point.Y) &&
                point.X < ((pj.X - pi.X) * (point.Y - pi.Y) / denominator) + pi.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static string DescribeTier(RecognitionConfidenceTier tier) => tier switch
    {
        RecognitionConfidenceTier.High => "高",
        RecognitionConfidenceTier.Medium => "中（建议复核）",
        RecognitionConfidenceTier.Low => "低（需要你选择）",
        _ => "无"
    };
}
