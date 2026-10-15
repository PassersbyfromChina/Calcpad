using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.History;

/// <summary>Geometry of one history row.</summary>
public sealed record HistoryRowLayout(int Index, CRect Row, CRect ExpressionArea, CRect ResultArea, CRect ReinsertButton);

/// <summary>Pure geometry of the history widget.</summary>
public sealed class HistoryLayout
{
    /// <summary>Title strip.</summary>
    public CRect Title { get; init; }

    /// <summary>Small button in the title row that opens and closes the search field.</summary>
    public CRect SearchToggle { get; init; }

    /// <summary>Search field; empty when search is collapsed.</summary>
    public CRect SearchField { get; init; }

    /// <summary>Area the rows are drawn into.</summary>
    public CRect ListArea { get; init; }

    /// <summary>Visible rows.</summary>
    public IReadOnlyList<HistoryRowLayout> Rows { get; init; } = [];

    /// <summary>Placeholder area shown when there is nothing to list.</summary>
    public CRect EmptyState { get; init; }

    /// <summary>Clear button.</summary>
    public CRect ClearButton { get; init; }

    /// <summary>Refresh button.</summary>
    public CRect RefreshButton { get; init; }

    /// <summary>First entry index that is visible.</summary>
    public int FirstVisible { get; init; }

    /// <summary>Computes the layout for a widget of the given logical size.</summary>
    public static HistoryLayout Compute(CSize size, int itemCount, bool showSearch, int selected, int scroll)
    {
        const double padding = 12;
        var width = Math.Max(200, size.Width);
        var height = Math.Max(160, size.Height);
        var title = new CRect(padding, 8, width - (padding * 2), 28);
        var searchField = showSearch
            ? new CRect(title.X, title.Bottom + 2, title.Width, 30)
            : CRect.Empty;
        var listTop = showSearch ? searchField.Bottom + 6 : title.Bottom + 4;
        var footerHeight = 40d;
        var footerTop = height - padding - footerHeight;
        var listArea = new CRect(title.X, listTop, title.Width, Math.Max(20, footerTop - listTop - 6));

        var rowHeight = 54d;
        var visible = Math.Max(1, (int)(listArea.Height / rowHeight));
        var maxScroll = Math.Max(0, itemCount - visible);
        var first = Math.Clamp(scroll, 0, maxScroll);
        if (selected >= 0)
        {
            if (selected < first)
            {
                first = selected;
            }
            else if (selected >= first + visible)
            {
                first = Math.Clamp(selected - visible + 1, 0, maxScroll);
            }
        }

        var rows = new List<HistoryRowLayout>();
        for (var i = first; i < Math.Min(itemCount, first + visible); i++)
        {
            var row = new CRect(listArea.X, listArea.Y + ((i - first) * rowHeight), listArea.Width, rowHeight - 4);
            var buttonWidth = Math.Min(54, row.Width * 0.22);
            rows.Add(new HistoryRowLayout(
                i,
                row,
                new CRect(row.X + 8, row.Y + 2, Math.Max(20, row.Width - buttonWidth - 20), row.Height * 0.5),
                new CRect(row.X + 8, row.Y + (row.Height * 0.5), Math.Max(20, row.Width - buttonWidth - 20), row.Height * 0.5 - 2),
                new CRect(row.Right - buttonWidth - 6, row.Y + ((row.Height - 26) / 2), buttonWidth, 26)));
        }

        var gap = 8d;
        var buttonWidth2 = (title.Width - gap) / 2;
        return new HistoryLayout
        {
            Title = title,
            SearchToggle = new CRect(title.Right - 58, title.Y + 3, 58, 22),
            SearchField = searchField,
            ListArea = listArea,
            Rows = rows,
            EmptyState = listArea,
            ClearButton = new CRect(title.X, footerTop, buttonWidth2, footerHeight),
            RefreshButton = new CRect(title.X + buttonWidth2 + gap, footerTop, buttonWidth2, footerHeight),
            FirstVisible = first
        };
    }
}

/// <summary>
/// The persisted calculation history as a canvas widget: newest first, searchable, with one tap
/// reinserting an entry onto the canvas and a clear button.
/// </summary>
public sealed class HistoryWidget : IWidgetRenderer
{
    private static readonly Typeface Face =
        new(new FontFamily("Inter, Segoe UI, Noto Sans CJK SC, sans-serif"));

    private static readonly Typeface BoldFace =
        new(new FontFamily("Inter, Segoe UI, Noto Sans CJK SC, sans-serif"), FontStyle.Normal, FontWeight.SemiBold);

    private readonly HistoryStore _store;
    private readonly List<(WidgetObject Target, IWidgetHost Host, EventHandler Handler)> _subscriptions = [];

    /// <summary>Creates the widget over a history store.</summary>
    public HistoryWidget(HistoryStore store) => _store = store;

    /// <inheritdoc />
    public string WidgetType => "calcpad.history";

    /// <summary>The store backing this widget.</summary>
    public HistoryStore Store => _store;

    /// <inheritdoc />
    public CSize Measure(WidgetMeasureContext context) => new(360, 520);

    /// <inheritdoc />
    public void OnAttached(WidgetObject target, IWidgetHost host)
    {
        InitializeState(target);
        Subscribe(target, host);
        _ = RefreshAsync(host);
    }

    /// <inheritdoc />
    public void OnDetached(WidgetObject target, IWidgetHost host)
    {
        foreach (var subscription in _subscriptions.Where(s => ReferenceEquals(s.Target, target)).ToList())
        {
            _store.Changed -= subscription.Handler;
            _subscriptions.Remove(subscription);
        }
    }

    /// <summary>Initialises <see cref="WidgetObject.State"/> without needing a live host.</summary>
    public void InitializeState(WidgetObject target)
    {
        target.State ??= new JsonObject();
        var state = target.State;
        state["query"] ??= string.Empty;
        state["selected"] ??= -1;
        state["scroll"] ??= 0;
        state["showSearch"] ??= false;
        state["hint"] ??= null;
        state["pressed"] ??= null;
        state["updatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public void Render(WidgetRenderContext context)
    {
        var size = context.LogicalSize;
        var theme = context.Theme;
        var drawing = context.Drawing;
        var state = context.Object.State ?? new JsonObject();
        var query = ReadString(state, "query") ?? string.Empty;
        var selected = ReadInt(state, "selected", -1);
        var scroll = ReadInt(state, "scroll", 0);
        var showSearch = ReadBool(state, "showSearch");
        var entries = _store.Search(query, 200);
        var layout = HistoryLayout.Compute(size, entries.Count, showSearch, selected, scroll);
        var pressed = ReadString(state, "pressed");

        drawing.DrawRectangle(theme.SurfaceBrush, null, new RoundedRect(new Rect(0, 0, size.Width, size.Height), 20));
        DrawText(drawing, "历史记录 / History", layout.Title, 16, theme.Brush(theme.TextPrimary), Align.Left, true);
        DrawText(
            drawing,
            $"{entries.Count} 条 / items",
            new CRect(layout.Title.X, layout.Title.Y, Math.Max(10, layout.SearchToggle.X - layout.Title.X - 6), layout.Title.Height),
            11,
            theme.Brush(theme.TextSecondary),
            Align.Right);

        var toggleFill = showSearch
            ? theme.Accent
            : string.Equals(pressed, "search", StringComparison.Ordinal) ? Lighten(theme.SurfaceRaised, 0.35) : theme.SurfaceRaised;
        Panel(drawing, layout.SearchToggle, theme.Brush(toggleFill), 8);
        DrawText(
            drawing,
            showSearch ? "关闭" : "搜索",
            layout.SearchToggle,
            11,
            theme.Brush(showSearch ? theme.AccentText : theme.TextPrimary),
            Align.Center,
            true);

        if (showSearch && layout.SearchField.Height > 1)
        {
            Panel(drawing, layout.SearchField, theme.Brush(theme.SurfaceAlt), 10);
            var text = query.Length == 0 ? "搜索表达式或结果 / Search" : query;
            DrawText(
                drawing,
                text,
                layout.SearchField,
                12,
                theme.Brush(query.Length == 0 ? theme.TextSecondary : theme.TextPrimary),
                Align.Left);
        }

        if (entries.Count == 0)
        {
            DrawText(
                drawing,
                query.Length == 0 ? "暂无历史 / No history yet" : "没有匹配项 / No matches",
                layout.EmptyState,
                12,
                theme.Brush(theme.TextSecondary),
                Align.Center);
        }

        foreach (var row in layout.Rows)
        {
            var entry = entries[row.Index];
            var isSelected = row.Index == selected;
            Panel(drawing, row.Row, theme.Brush(isSelected ? theme.SurfaceRaised : theme.SurfaceAlt), 10);
            DrawFitted(drawing, entry.Expression, row.ExpressionArea, 12.5, 9, theme.Brush(theme.TextPrimary), Align.Left, isSelected);
            DrawFitted(
                drawing,
                entry.IsError ? "⚠ " + (entry.Error ?? "Error") : "= " + entry.Result,
                row.ResultArea,
                11.5,
                8,
                theme.Brush(entry.IsError ? theme.Danger : theme.Accent),
                Align.Left);

            var reinsertFill = string.Equals(pressed, $"reinsert:{row.Index}", StringComparison.Ordinal)
                ? Lighten(theme.SurfaceRaised, 0.35)
                : theme.SurfaceRaised;
            Panel(drawing, row.ReinsertButton, theme.Brush(reinsertFill), 8);
            DrawText(drawing, "插入", row.ReinsertButton, 11, theme.Brush(theme.TextPrimary), Align.Center, true);
        }

        DrawButton(drawing, theme, layout.ClearButton, "清空 / Clear", theme.Danger, Color.Parse("#FFFFFFFF"), string.Equals(pressed, "clear", StringComparison.Ordinal));
        DrawButton(drawing, theme, layout.RefreshButton, "刷新 / Refresh", theme.SurfaceRaised, theme.TextPrimary, string.Equals(pressed, "refresh", StringComparison.Ordinal));

        if (ReadString(state, "hint") is { Length: > 0 } hint)
        {
            DrawText(
                drawing,
                hint,
                new CRect(layout.Title.X, layout.ClearButton.Y - 16, layout.Title.Width, 14),
                10,
                theme.Brush(theme.TextSecondary),
                Align.Right);
        }

        if (context.IsSelected)
        {
            drawing.DrawRectangle(
                null,
                new Pen(theme.Brush(theme.Selection), 2),
                new RoundedRect(new Rect(1, 1, Math.Max(1, size.Width - 2), Math.Max(1, size.Height - 2)), 20));
        }
    }

    /// <inheritdoc />
    public bool OnPointer(WidgetPointerContext context)
    {
        var size = context.LogicalSize;
        var theme = context.Host.Theme;
        var state = context.Object.State ?? new JsonObject();
        var query = ReadString(state, "query");
        var selected = ReadInt(state, "selected", -1);
        var scroll = ReadInt(state, "scroll", 0);
        var showSearch = ReadBool(state, "showSearch");
        var entries = _store.Search(query, 200);
        var layout = HistoryLayout.Compute(size, entries.Count, showSearch, selected, scroll);

        switch (context.Phase)
        {
            case WidgetPointerPhase.Down:
                {
                    var action = Hit(layout, context.Position);
                    if (action is null)
                    {
                        return false;
                    }

                    SetString(context.Object, "pressed", action);
                    context.Host.Invalidate();
                    return true;
                }

            case WidgetPointerPhase.Up:
                {
                    var action = ReadString(state, "pressed");
                    if (action is null)
                    {
                        return false;
                    }

                    SetString(context.Object, "pressed", null);
                    if (string.Equals(Hit(layout, context.Position), action, StringComparison.Ordinal))
                    {
                        Perform(context, entries, layout, action);
                    }

                    context.Host.Invalidate();
                    return true;
                }

            case WidgetPointerPhase.Cancel:
                SetString(context.Object, "pressed", null);
                context.Host.Invalidate();
                return true;

            case WidgetPointerPhase.Wheel:
                if (entries.Count == 0)
                {
                    return false;
                }

                var direction = context.WheelDelta > 0 ? -1 : 1;
                var next = Math.Max(0, scroll + direction);
                SetInt(context.Object, "scroll", Math.Min(next, Math.Max(0, entries.Count - 1)));
                context.Host.Invalidate();
                return true;

            default:
                return false;
        }
    }

    /// <inheritdoc />
    public bool OnKey(WidgetKeyContext context)
    {
        var state = context.Object.State ?? new JsonObject();
        var key = context.Key ?? string.Empty;
        var query = ReadString(state, "query") ?? string.Empty;
        var selected = ReadInt(state, "selected", -1);
        var entries = _store.Search(query, 200);

        switch (key)
        {
            case "Back" or "BackSpace" or "Backspace":
                if (query.Length > 0)
                {
                    SetString(context.Object, "query", query[..^1]);
                }
                else
                {
                    return false;
                }

                break;
            case "Escape" or "Clear":
                SetString(context.Object, "query", string.Empty);
                SetBool(context.Object, "showSearch", false);
                break;
            case "Up":
                SetInt(context.Object, "selected", Math.Max(0, selected - 1));
                break;
            case "Down":
                SetInt(context.Object, "selected", Math.Min(Math.Max(0, entries.Count - 1), selected + 1));
                break;
            case "Return" or "Enter":
                if (selected >= 0 && selected < entries.Count)
                {
                    _ = ReinsertAsync(context, entries[selected]);
                }
                else
                {
                    return false;
                }

                break;
            default:
                if (key.Length == 1 && !char.IsControl(key[0]))
                {
                    SetString(context.Object, "query", query + key);
                }
                else
                {
                    return false;
                }

                break;
        }

        context.Host.Invalidate();
        return true;
    }

    private static string? Hit(HistoryLayout layout, CPoint point)
    {
        if (layout.ClearButton.Contains(point))
        {
            return "clear";
        }

        if (layout.RefreshButton.Contains(point))
        {
            return "refresh";
        }

        foreach (var row in layout.Rows)
        {
            if (row.ReinsertButton.Contains(point))
            {
                return $"reinsert:{row.Index}";
            }

            if (row.Row.Contains(point))
            {
                return $"row:{row.Index}";
            }
        }

        return layout.SearchField.Height > 1 && layout.SearchField.Contains(point) || layout.SearchToggle.Contains(point)
            ? "search"
            : null;
    }

    private void Perform(WidgetPointerContext context, IReadOnlyList<HistoryEntry> entries, HistoryLayout layout, string action)
    {
        if (action.StartsWith("row:", StringComparison.Ordinal) && int.TryParse(action[4..], out var rowIndex))
        {
            SetInt(context.Object, "selected", rowIndex);
            return;
        }

        if (action.StartsWith("reinsert:", StringComparison.Ordinal) && int.TryParse(action[9..], out var reinsertIndex))
        {
            if (reinsertIndex >= 0 && reinsertIndex < entries.Count)
            {
                _ = ReinsertAsync(context, entries[reinsertIndex]);
            }

            return;
        }

        switch (action)
        {
            case "search":
                SetBool(context.Object, "showSearch", !ReadBool(context.Object.State, "showSearch"));
                break;
            case "refresh":
                _ = RefreshAsync(context.Host);
                break;
            case "clear":
                _ = ConfirmAndClearAsync(context);
                break;
        }

        _ = layout;
    }

    private async Task RefreshAsync(IWidgetHost host)
    {
        try
        {
            await _store.RefreshAsync(200).ConfigureAwait(true);
            await host.Host.DispatchAsync(host.Invalidate).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // A missing database must not break the widget; the cache keeps working.
        }
    }

    private async Task ConfirmAndClearAsync(WidgetPointerContext context)
    {
        try
        {
            var confirmed = await context.Host.Host
                .ConfirmAsync("清空历史 / Clear history", "确定要删除全部历史记录吗？/ Delete every recorded calculation?")
                .ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }

            await _store.ClearAsync().ConfigureAwait(true);
            SetInt(context.Object, "selected", -1);
            SetInt(context.Object, "scroll", 0);
            context.Host.Invalidate();
        }
        catch (Exception)
        {
            SetString(context.Object, "hint", "清空失败 / clear failed");
            context.Host.Invalidate();
        }
    }

    private async Task ReinsertAsync(WidgetKeyContext context, HistoryEntry entry)
    {
        try
        {
            var item = new MathObject
            {
                Expression = entry.Expression,
                Result = entry.Result,
                IsError = entry.IsError,
                ErrorMessage = entry.Error,
                OwnerPluginId = "calcpad.history",
                Bounds = new CRect(context.Object.Bounds.Right + 24, context.Object.Bounds.Y, 240, 56)
            };
            await context.Host.Canvas.AddAsync(item).ConfigureAwait(true);
            await context.Host.Canvas.RevealAsync(item.Bounds).ConfigureAwait(true);
            SetString(context.Object, "hint", "已插入画布 / inserted");
        }
        catch (Exception)
        {
            SetString(context.Object, "hint", "插入失败 / insert failed");
        }

        context.Host.Invalidate();
    }

    private async Task ReinsertAsync(WidgetPointerContext context, HistoryEntry entry)
    {
        try
        {
            var item = new MathObject
            {
                Expression = entry.Expression,
                Result = entry.Result,
                IsError = entry.IsError,
                ErrorMessage = entry.Error,
                OwnerPluginId = "calcpad.history",
                Bounds = new CRect(context.Object.Bounds.Right + 24, context.Object.Bounds.Y, 240, 56)
            };
            await context.Host.Canvas.AddAsync(item).ConfigureAwait(true);
            await context.Host.Canvas.RevealAsync(item.Bounds).ConfigureAwait(true);
            SetString(context.Object, "hint", "已插入画布 / inserted");
        }
        catch (Exception)
        {
            SetString(context.Object, "hint", "插入失败 / insert failed");
        }

        context.Host.Invalidate();
    }

    private void Subscribe(WidgetObject target, IWidgetHost host)
    {
        if (_subscriptions.Any(s => ReferenceEquals(s.Target, target)))
        {
            return;
        }

        EventHandler handler = (_, _) => host.Invalidate();
        _store.Changed += handler;
        _subscriptions.Add((target, host, handler));
    }

    private static void Panel(DrawingContext drawing, CRect rect, IBrush fill, double radius) =>
        drawing.DrawRectangle(fill, null, new RoundedRect(new Rect(rect.X, rect.Y, Math.Max(1, rect.Width), Math.Max(1, rect.Height)), radius));

    private static void DrawButton(
        DrawingContext drawing,
        PluginTheme theme,
        CRect rect,
        string label,
        Color fill,
        Color text,
        bool pressed)
    {
        Panel(drawing, rect, theme.Brush(pressed ? Lighten(fill, 0.3) : fill), 10);
        DrawText(drawing, label, rect, 12, theme.Brush(text), Align.Center, true);
    }

    private static void DrawText(
        DrawingContext drawing,
        string text,
        CRect area,
        double size,
        IBrush brush,
        Align align,
        bool bold = false)
    {
        if (string.IsNullOrEmpty(text) || area.Width <= 2 || area.Height <= 2)
        {
            return;
        }

        using var clip = drawing.PushClip(new Rect(area.X, area.Y, area.Width, area.Height).Inflate(1));
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, brush);
        var x = align switch
        {
            Align.Left => area.X + 4,
            Align.Right => area.Right - formatted.Width - 4,
            _ => area.X + ((area.Width - formatted.Width) / 2)
        };
        drawing.DrawText(formatted, new Point(x, area.Y + ((area.Height - formatted.Height) / 2)));
    }

    private static void DrawFitted(
        DrawingContext drawing,
        string text,
        CRect area,
        double maxSize,
        double minSize,
        IBrush brush,
        Align align,
        bool bold = false)
    {
        if (string.IsNullOrEmpty(text) || area.Width <= 2 || area.Height <= 2)
        {
            return;
        }

        var size = Math.Max(minSize, maxSize);
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, brush);
        while (formatted.Width > area.Width - 6 && size > minSize)
        {
            size -= 0.5;
            formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, brush);
        }

        using var clip = drawing.PushClip(new Rect(area.X, area.Y, area.Width, area.Height).Inflate(1));
        var x = align switch
        {
            Align.Left => area.X + 3,
            Align.Right => area.Right - formatted.Width - 3,
            _ => area.X + ((area.Width - formatted.Width) / 2)
        };
        drawing.DrawText(formatted, new Point(x, area.Y + ((area.Height - formatted.Height) / 2)));
    }

    private static Color Lighten(Color color, double amount) => Color.FromArgb(
        color.A,
        (byte)Math.Clamp(color.R + ((255 - color.R) * amount), 0, 255),
        (byte)Math.Clamp(color.G + ((255 - color.G) * amount), 0, 255),
        (byte)Math.Clamp(color.B + ((255 - color.B) * amount), 0, 255));

    private static string? ReadString(JsonObject? state, string key) =>
        state is not null && state.TryGetPropertyValue(key, out var node) && node is not null ? node.ToString() : null;

    private static int ReadInt(JsonObject? state, string key, int fallback) =>
        state is not null && state.TryGetPropertyValue(key, out var node) && node is not null &&
        int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool ReadBool(JsonObject? state, string key) =>
        state is not null && state.TryGetPropertyValue(key, out var node) && node is not null &&
        bool.TryParse(node.ToString(), out var value) && value;

    private static void SetString(WidgetObject target, string key, string? value)
    {
        target.State ??= new JsonObject();
        target.State[key] = value;
    }

    private static void SetInt(WidgetObject target, string key, int value)
    {
        target.State ??= new JsonObject();
        target.State[key] = value;
    }

    private static void SetBool(WidgetObject target, string key, bool value)
    {
        target.State ??= new JsonObject();
        target.State[key] = value;
    }

    private enum Align
    {
        Left,
        Center,
        Right
    }
}
