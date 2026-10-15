using System.Diagnostics;
using Calcpad.Core.Ai;
using Calcpad.Core.Canvas;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;
using Calcpad.Plugin.AskAi.Ui;
using Calcpad.Plugin.Sdk.Ui;
using PluginCommandContext = Calcpad.Core.Plugins.CommandContext;

namespace Calcpad.Plugin.AskAi;

/// <summary>
/// Lasso anything on the canvas and ask an AI about it.
/// <para>
/// The plugin never sends anything the user has not seen: the preview panel shows the exact text, the
/// image thumbnail and the byte count, the destination endpoint and model, and lets the user drop the
/// image or edit the text. The host's egress guard reviews the same payload again before the transport
/// runs. Whole canvas mode is text only and per object opt in — it never uploads a canvas screenshot.
/// </para>
/// </summary>
public sealed class AskAiPlugin : IPlugin
{
    private readonly HashSet<PermissionKind> _granted = [];
    private IPluginContext? _context;
    private Stopwatch _streamClock = new();
    private DateTimeOffset _lastCardUpdate = DateTimeOffset.MinValue;

    /// <inheritdoc />
    public PluginManifest Manifest { get; } = new()
    {
        Id = "calcpad.askai",
        Name = "问 AI",
        Version = "0.0.0",
        Description = "圈画画布上的任何内容问 AI。发送前会给你看确切的上传内容；本地模型优先，整画布只发你勾选的文字。",
        Author = "PassersbyfromChina",
        Keywords = ["ai", "ask", "chat", "问", "圈画", "解释"],
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
            new MenuContribution("AI/问 AI", "圈画内容问 AI", AskAiCommands.AskRegion,
                Shortcut: "Ctrl+Shift+A", Order: 10, LocalizedTitleZh: "圈画内容问 AI"),
            new MenuContribution("AI/问 AI", "对当前选中内容提问", AskAiCommands.AskSelection, Order: 20, LocalizedTitleZh: "对当前选中内容提问"),
            new MenuContribution("AI/问 AI", "整画布提问（只发勾选文字）", AskAiCommands.AskWholeCanvas, Order: 30, LocalizedTitleZh: "整画布提问（只发勾选文字）"),
            new MenuContribution("AI/问 AI", "重新生成上一个回答", AskAiCommands.Regenerate, Order: 40, LocalizedTitleZh: "重新生成上一个回答"),
            new MenuContribution("AI/问 AI", "问 AI 设置…", AskAiCommands.OpenSettings, Order: 90, LocalizedTitleZh: "问 AI 设置…")
        ],
        Shortcuts =
        [
            new ShortcutContribution("Ctrl+Shift+A", AskAiCommands.AskRegion, "圈画内容问 AI", LocalizedTitleZh: "圈画内容问 AI"),
            new ShortcutContribution("Ctrl+Shift+K", AskAiCommands.AskWholeCanvas, "整画布提问", LocalizedTitleZh: "整画布提问")
        ],
        Panels =
        [
            new PanelContribution(AskAiCommands.PreviewPanelId, "问 AI", AskAiCommands.OpenPreview,
                PanelPlacement.Floating, Order: 10, PreferredWidth: 460, LocalizedTitleZh: "问 AI"),
            new PanelContribution(AskAiCommands.SettingsPanelId, "问 AI 设置", AskAiCommands.OpenSettings,
                PanelPlacement.Right, Order: 90, PreferredWidth: 400, LocalizedTitleZh: "问 AI 设置")
        ],
        Tools =
        [
            new ToolContribution("askai.lasso", "圈画问 AI", AskAiCommands.AskRegion, Shortcut: "A", Order: 10, LocalizedTitleZh: "圈画问 AI")
        ],
        EnabledByDefault = true
    };

    /// <summary>Settings currently in force.</summary>
    public AskAiSettings Settings { get; private set; } = new();

    /// <summary>Draft being reviewed, or null.</summary>
    public AskAiDraft? Draft { get; private set; }

    /// <summary>Card object that shows the current answer, when one exists.</summary>
    public string? ActiveCardId { get; private set; }

    /// <summary>Last question, kept for "regenerate" and for follow-ups.</summary>
    public string LastQuestion { get; private set; } = string.Empty;

    /// <summary>Last answer text.</summary>
    public string LastAnswer { get; private set; } = string.Empty;

    /// <summary>Object ids the last question was about.</summary>
    public IReadOnlyList<string> LastSourceObjectIds { get; private set; } = [];

    /// <summary>World region of the last question.</summary>
    public CRect? LastRegion { get; private set; }

    /// <summary>True while a request is streaming.</summary>
    public bool IsStreaming { get; private set; }

    /// <summary>Last error or status note shown in the panel.</summary>
    public string? Status { get; private set; }

    /// <summary>Theme reported by the shell.</summary>
    public PluginTheme Theme { get; private set; } = PluginTheme.IpadDark;

    /// <summary>Raised when the draft, the answer or the streaming state changes.</summary>
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public ValueTask InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Settings = AskAiSettings.Load(context.Settings);

        if (context is IPluginUiContext ui)
        {
            Theme = ui.Ui.Theme;
            ui.Ui.RegisterPanel(AskAiCommands.PreviewPanelId, () => new AskAiPreviewPanel(this).Build());
            ui.Ui.RegisterPanel(AskAiCommands.SettingsPanelId, () => new AskAiSettingsPanel(this).Build());
            ui.Ui.RegisterOverlay(AskAiCommands.ActionBarId, () => new AskAiActionBar(this).Build());
        }
        else
        {
            context.Log.Info("Ask AI plugin started without a UI registry; commands remain available.");
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
        switch (context.CommandId)
        {
            case AskAiCommands.AskRegion:
                await AskRegionAsync(ct).ConfigureAwait(false);
                break;

            case AskAiCommands.AskSelection:
                await AskSelectionAsync(ct).ConfigureAwait(false);
                break;

            case AskAiCommands.AskWholeCanvas:
                await AskWholeCanvasAsync(ct).ConfigureAwait(false);
                break;

            case AskAiCommands.OpenPreview:
                await OpenPreviewAsync(ct).ConfigureAwait(false);
                break;

            case AskAiCommands.Regenerate:
                await RegenerateAsync(ct).ConfigureAwait(false);
                break;

            case AskAiCommands.InsertAnswerAsText:
                await InsertAnswerAsTextAsync(ct).ConfigureAwait(false);
                break;

            case AskAiCommands.DeleteCard:
                await DeleteCardAsync(ct).ConfigureAwait(false);
                break;

            case AskAiCommands.OpenSettings:
                if (_context is { } plugin)
                {
                    await plugin.Host.OpenPanelAsync(plugin.PluginId, AskAiCommands.SettingsPanelId, ct).ConfigureAwait(false);
                }

                break;

            default:
                _context?.Log.Debug($"Ask AI ignored unknown command '{context.CommandId}'.");
                break;
        }
    }

    /// <summary>Builds a draft from the lasso (or the selection) and shows the floating action bar.</summary>
    public async ValueTask AskRegionAsync(CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null || !await EnsureAsync(PermissionKind.CanvasRead, "提问需要读取你圈选的内容。", ct).ConfigureAwait(false))
        {
            return;
        }

        var lasso = await FindLassoAsync(ct).ConfigureAwait(false);
        IReadOnlyList<string> ids;
        CRect? region = null;
        if (lasso is not null)
        {
            region = CRect.FromPoints(lasso.Points);
            ids = lasso.CapturedObjectIds.Count > 0
                ? [.. lasso.CapturedObjectIds]
                : await ObjectIdsInAsync(region.Value, ct).ConfigureAwait(false);
        }
        else
        {
            ids = [.. plugin.Canvas.Selection];
            foreach (var id in ids)
            {
                var obj = await plugin.Canvas.GetAsync(id, ct).ConfigureAwait(false);
                if (obj is not null)
                {
                    region = region is { } current ? current.Union(obj.Bounds) : obj.Bounds;
                }
            }
        }

        if (ids.Count == 0)
        {
            await plugin.Host.ShowToastAsync("请先用套索圈出要提问的内容（或选中对象）", ToastLevel.Info, ct).ConfigureAwait(false);
            return;
        }

        Draft = await BuildDraftAsync(ids, region, ct).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);

        // Hide the temporary lasso so it does not become part of the content.
        if (lasso is not null)
        {
            await plugin.Canvas.RemoveAsync(lasso.Id, ct).ConfigureAwait(false);
        }

        await plugin.Host.OpenPanelAsync(plugin.PluginId, AskAiCommands.ActionBarId, ct).ConfigureAwait(false);
        plugin.Log.Info($"Ask AI draft ready: {Draft.Describe()}");
    }

    /// <summary>Draft from the current selection without needing a lasso.</summary>
    public async ValueTask AskSelectionAsync(CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null || !await EnsureAsync(PermissionKind.CanvasRead, "提问需要读取你选中的内容。", ct).ConfigureAwait(false))
        {
            return;
        }

        var ids = plugin.Canvas.Selection.ToList();
        if (ids.Count == 0)
        {
            await plugin.Host.ShowToastAsync("没有选中任何对象", ToastLevel.Warning, ct).ConfigureAwait(false);
            return;
        }

        CRect? region = null;
        foreach (var id in ids)
        {
            var obj = await plugin.Canvas.GetAsync(id, ct).ConfigureAwait(false);
            if (obj is not null)
            {
                region = region is { } current ? current.Union(obj.Bounds) : obj.Bounds;
            }
        }

        Draft = await BuildDraftAsync(ids, region, ct).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
        await OpenPreviewAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whole canvas mode: builds a text only draft from the objects the user ticks. No screenshot is
    /// ever produced here, which is what keeps "whole canvas" from becoming one big upload.
    /// </summary>
    public async ValueTask AskWholeCanvasAsync(CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null || !await EnsureAsync(PermissionKind.CanvasRead, "提问需要读取画布上的文字。", ct).ConfigureAwait(false))
        {
            return;
        }

        var objects = await plugin.Canvas.QueryAsync(CanvasQuery.Everything, ct).ConfigureAwait(false);
        var texts = objects
            .Where(o => o.Kind is ObjectKinds.Text or ObjectKinds.Math or ObjectKinds.AiCard or ObjectKinds.Widget)
            .Select(o => (Object: o, Text: PlainText(o)))
            .Where(t => !string.IsNullOrWhiteSpace(t.Text))
            .ToList();

        if (texts.Count == 0)
        {
            await plugin.Host.ShowToastAsync("画布上没有可用的文字内容", ToastLevel.Info, ct).ConfigureAwait(false);
            return;
        }

        Draft = new AskAiDraft
        {
            SourceObjectIds = [.. texts.Select(t => t.Object.Id)],
            ContextText = string.Join("\n", texts.Select(t => t.Text)),
            ContextPng = null,
            IncludeText = true,
            IncludeImage = false,
            PreferLocal = Settings.PreferOnDevice
        };
        StateChanged?.Invoke(this, EventArgs.Empty);
        await OpenPreviewAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Shows the payload preview panel for the current draft.</summary>
    public async ValueTask OpenPreviewAsync(CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null || Draft is null)
        {
            return;
        }

        await plugin.Host.OpenPanelAsync(plugin.PluginId, AskAiCommands.PreviewPanelId, ct).ConfigureAwait(false);
    }

    /// <summary>Sends the draft after the user confirmed it, streaming the answer into a card.</summary>
    public async ValueTask SendAsync(string question, CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null || Draft is null)
        {
            return;
        }

        if (!await EnsureAsync(PermissionKind.CanvasWrite, "回答会作为卡片放回画布，需要修改画布。", ct).ConfigureAwait(false))
        {
            return;
        }

        if (!await EnsureAsync(PermissionKind.AiLocal, "提问可以在本机完成，不会联网。", ct).ConfigureAwait(false))
        {
            return;
        }

        var capabilities = plugin.Ai.Capabilities;
        var needsCloud = !capabilities.OnDeviceChat;
        if (needsCloud)
        {
            if (!capabilities.CloudConfigured)
            {
                Status = "没有可用的 AI 服务：既没有本地模型端点，也没有配置云供应商。";
                StateChanged?.Invoke(this, EventArgs.Empty);
                await plugin.Host.ShowToastAsync(Status, ToastLevel.Warning, ct).ConfigureAwait(false);
                return;
            }

            if (!capabilities.UploadEnabled)
            {
                Status = "云上传已被全局关闭（IEgressGuard），无法发送。";
                StateChanged?.Invoke(this, EventArgs.Empty);
                await plugin.Host.ShowToastAsync(Status, ToastLevel.Warning, ct).ConfigureAwait(false);
                return;
            }

            if (!await EnsureAsync(PermissionKind.AiCloud, "当前没有本地模型，提问需要发送到云端；每次发送前你都会看到完整内容。", ct).ConfigureAwait(false))
            {
                Status = "未获得云端 AI 授权。";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        Draft.Question = question ?? string.Empty;
        if (Draft.TotalBytes == 0)
        {
            Status = "没有内容可发送。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        LastQuestion = Draft.Question;
        LastSourceObjectIds = Draft.SourceObjectIds;
        LastRegion = Draft.Region;
        LastAnswer = string.Empty;
        Status = null;
        IsStreaming = true;
        StateChanged?.Invoke(this, EventArgs.Empty);

        var cardId = await EnsureCardAsync(ct).ConfigureAwait(false);
        ActiveCardId = cardId;

        _streamClock = Stopwatch.StartNew();
        _lastCardUpdate = DateTimeOffset.MinValue;

        var progress = new CardProgress(this, cardId);
        AiAnswer answer;
        try
        {
            answer = await plugin.Ai
                .AskAsync(Draft.ToRequest(allowCloud: needsCloud || !Draft.PreferLocal), progress, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            plugin.Log.Error("Ask AI request failed", ex);
            answer = new AiAnswer(string.Empty, "none", null, false, false, $"{ex.GetType().Name}：{ex.Message}");
        }

        IsStreaming = false;
        LastAnswer = answer.Text;
        Status = answer.Refused
            ? $"已拒绝发送：{answer.Reason}"
            : answer.Text.Length == 0
                ? $"没有回答：{answer.Reason}"
                : answer.Reason;

        await UpdateCardAsync(cardId, card =>
        {
            card.Question = LastQuestion;
            card.Answer = answer.Text.Length > 0 ? answer.Text : $"（未获得回答：{answer.Reason}）";
            card.Provider = answer.Provider;
            card.Model = answer.Model;
            card.WasUploaded = answer.UsedCloud;
            card.UploadedExcerpt = answer.UsedCloud ? Draft.ContextText : null;
            card.IsStreaming = false;
        }, ct).ConfigureAwait(false);

        StateChanged?.Invoke(this, EventArgs.Empty);
        await plugin.Host.ShowToastAsync(
            answer.Refused ? $"已拒绝上传：{answer.Reason}" : "回答已放到画布上",
            answer.Refused ? ToastLevel.Warning : ToastLevel.Success,
            ct).ConfigureAwait(false);
    }

    /// <summary>Asks a follow-up question about the same card.</summary>
    public async ValueTask FollowUpAsync(string question, CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null || string.IsNullOrWhiteSpace(question))
        {
            return;
        }

        // The follow-up carries the previous answer as context; the draft keeps the original sources.
        Draft ??= new AskAiDraft();
        Draft.ContextText = string.IsNullOrWhiteSpace(LastAnswer)
            ? Draft.ContextText
            : $"{Draft.ContextText}\n\n上一轮回答：{LastAnswer}";
        Draft.IncludeText = true;
        await SendAsync(question, ct).ConfigureAwait(false);
    }

    /// <summary>Asks the last question again.</summary>
    public async ValueTask RegenerateAsync(CancellationToken ct = default)
    {
        if (Draft is null || string.IsNullOrEmpty(LastQuestion))
        {
            return;
        }

        await SendAsync(LastQuestion, ct).ConfigureAwait(false);
    }

    /// <summary>Puts the answer on the canvas as a plain editable text object.</summary>
    public async ValueTask InsertAnswerAsTextAsync(CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null || string.IsNullOrEmpty(LastAnswer))
        {
            return;
        }

        if (!await EnsureAsync(PermissionKind.CanvasWrite, "插入文本需要修改画布。", ct).ConfigureAwait(false))
        {
            return;
        }

        var anchor = LastRegion ?? CRect.Empty;
        var text = new TextObject
        {
            Text = LastAnswer,
            Bounds = new CRect(anchor.IsEmpty ? 0 : anchor.X, anchor.IsEmpty ? 0 : anchor.Bottom + 24, 360, 120),
            OwnerPluginId = plugin.PluginId
        };
        text.RecomputeBounds();
        using var edit = await plugin.Canvas.BeginEditAsync("插入 AI 回答", ct).ConfigureAwait(false);
        await plugin.Canvas.AddAsync(text, ct).ConfigureAwait(false);
        await edit.CommitAsync(ct).ConfigureAwait(false);
        await plugin.Host.ShowToastAsync("已插入为新文本", ToastLevel.Success, ct).ConfigureAwait(false);
    }

    /// <summary>Removes the answer card from the canvas.</summary>
    public async ValueTask DeleteCardAsync(CancellationToken ct = default)
    {
        var plugin = _context;
        if (plugin is null || ActiveCardId is null)
        {
            return;
        }

        await plugin.Canvas.RemoveAsync(ActiveCardId, ct).ConfigureAwait(false);
        ActiveCardId = null;
        LastAnswer = string.Empty;
        StateChanged?.Invoke(this, EventArgs.Empty);
        await plugin.Host.ShowToastAsync("已删除回答卡片", ToastLevel.Info, ct).ConfigureAwait(false);
    }

    /// <summary>Persists settings from the settings panel.</summary>
    public async ValueTask UpdateSettingsAsync(AskAiSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        if (_context is { } plugin)
        {
            await Settings.SaveAsync(plugin.Settings, ct).ConfigureAwait(false);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Providers the user can pick from (the AI core decides which one actually runs).</summary>
    public IReadOnlyList<AiProviderInfo> Providers => _context?.Ai.Providers.Providers ?? [];

    /// <summary>Capabilities reported by the host.</summary>
    public AiCapabilities? Capabilities => _context?.Ai.Capabilities;

    /// <summary>True when the host has a local endpoint configured, so nothing would leave the device.</summary>
    public bool IsLocalOnly => Capabilities is { OnDeviceChat: true };

    /// <summary>The context handed over by the shell.</summary>
    public IPluginContext? Context => _context;

    /// <summary>Sets a status message shown in the panel.</summary>
    public void SetStatus(string? status)
    {
        Status = status;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Checks a permission, asking the user once per capability.</summary>
    public async ValueTask<bool> EnsureAsync(PermissionKind kind, string reason, CancellationToken ct = default)
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

        var decision = await plugin.Permissions
            .RequestAsync(new PermissionRequest(plugin.PluginId, Manifest.Name, [kind], reason), ct)
            .ConfigureAwait(false);

        if (decision.Granted)
        {
            _granted.Add(kind);
            return true;
        }

        await plugin.Host.ShowToastAsync($"未授予「{PermissionCatalog.Describe(kind).Title}」权限", ToastLevel.Warning, ct).ConfigureAwait(false);
        return false;
    }

    private async ValueTask<AskAiDraft> BuildDraftAsync(IReadOnlyList<string> ids, CRect? region, CancellationToken ct)
    {
        var plugin = _context!;
        var texts = new List<string>();
        foreach (var id in ids)
        {
            var obj = await plugin.Canvas.GetAsync(id, ct).ConfigureAwait(false);
            if (obj is null)
            {
                continue;
            }

            var text = PlainText(obj);
            if (!string.IsNullOrWhiteSpace(text))
            {
                texts.Add(text);
            }
        }

        byte[]? png = null;
        if (region is { IsEmpty: false } area)
        {
            var raster = await plugin.Canvas.RasterizeAsync(area, 2.0, ct).ConfigureAwait(false);
            png = raster?.Png is { Length: > 0 } bytes ? bytes : null;
        }

        return new AskAiDraft
        {
            SourceObjectIds = ids,
            Region = region,
            ContextText = string.Join("\n", texts),
            ContextPng = png,
            IncludeText = texts.Count > 0,
            IncludeImage = png is not null && Settings.IncludeImageByDefault,
            PreferLocal = Settings.PreferOnDevice
        };
    }

    private static string PlainText(CanvasObject obj) => obj switch
    {
        TextObject t => t.Text,
        MathObject m => string.IsNullOrEmpty(m.Result) ? m.Expression : $"{m.Expression} = {m.Result}",
        InkObject i => i.RecognizedText ?? string.Empty,
        AiCardObject a => $"{a.Question}\n{a.Answer}",
        WidgetObject w => w.Title,
        _ => string.Empty
    };

    private async ValueTask<ShapeObject?> FindLassoAsync(CancellationToken ct)
    {
        var plugin = _context;
        if (plugin is null)
        {
            return null;
        }

        var shapes = await plugin.Canvas.QueryAsync(new CanvasQuery { Kinds = [ObjectKinds.Shape] }, ct).ConfigureAwait(false);
        return shapes.OfType<ShapeObject>().Where(s => s.Points.Count >= 3).OrderByDescending(s => s.CreatedAt).FirstOrDefault();
    }

    private async ValueTask<IReadOnlyList<string>> ObjectIdsInAsync(CRect region, CancellationToken ct)
    {
        var plugin = _context;
        if (plugin is null)
        {
            return [];
        }

        var objects = await plugin.Canvas.QueryAsync(new CanvasQuery { Region = region }, ct).ConfigureAwait(false);
        return [.. objects.Where(o => o.Kind != ObjectKinds.Shape).Select(o => o.Id)];
    }

    private async ValueTask<string> EnsureCardAsync(CancellationToken ct)
    {
        var plugin = _context!;
        if (ActiveCardId is { Length: > 0 } existing &&
            await plugin.Canvas.GetAsync(existing, ct).ConfigureAwait(false) is AiCardObject)
        {
            return existing;
        }

        var anchor = LastRegion ?? CRect.Empty;
        var card = new AiCardObject
        {
            Question = LastQuestion,
            Answer = string.Empty,
            Provider = IsLocalOnly ? "on-device" : "cloud",
            IsStreaming = true,
            Bounds = new CRect(
                anchor.IsEmpty ? 40 : anchor.Right + 32,
                anchor.IsEmpty ? 40 : anchor.Y,
                360,
                180),
            OwnerPluginId = plugin.PluginId
        };
        card.RecomputeBounds();
        using var edit = await plugin.Canvas.BeginEditAsync("AI 回答", ct).ConfigureAwait(false);
        var id = await plugin.Canvas.AddAsync(card, ct).ConfigureAwait(false);
        await edit.CommitAsync(ct).ConfigureAwait(false);
        if (!anchor.IsEmpty)
        {
            await plugin.Canvas.RevealAsync(card.Bounds, ct).ConfigureAwait(false);
        }

        return id;
    }

    private async ValueTask UpdateCardAsync(string cardId, Action<AiCardObject> mutate, CancellationToken ct)
    {
        var plugin = _context;
        if (plugin is null)
        {
            return;
        }

        try
        {
            await plugin.Canvas.UpdateAsync(cardId, obj =>
            {
                if (obj is AiCardObject card)
                {
                    mutate(card);
                    card.RecomputeBounds();
                    card.Touch();
                }
            }, ct).ConfigureAwait(false);
        }
        catch (PermissionDeniedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            plugin.Log.Warn("Could not update the AI card", ex);
        }
    }

    /// <summary>Streams deltas into the card, throttled so the canvas is not updated per token.</summary>
    private sealed class CardProgress : IProgress<string>
    {
        private readonly AskAiPlugin _plugin;
        private readonly string _cardId;
        private readonly System.Text.StringBuilder _buffer = new();

        public CardProgress(AskAiPlugin plugin, string cardId)
        {
            _plugin = plugin;
            _cardId = cardId;
        }

        public void Report(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            _buffer.Append(value);
            var now = DateTimeOffset.UtcNow;
            var due = (now - _plugin._lastCardUpdate).TotalMilliseconds >= 90 || _buffer.Length > 0 && _plugin._streamClock.ElapsedMilliseconds > 4000;
            if (!due)
            {
                return;
            }

            _plugin._lastCardUpdate = now;
            var snapshot = _buffer.ToString();
            _ = _plugin.UpdateCardAsync(_cardId, card =>
            {
                card.Answer = snapshot;
                card.IsStreaming = true;
            }, CancellationToken.None);
        }
    }
}
