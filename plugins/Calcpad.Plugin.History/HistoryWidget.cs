using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;
using Calcpad.Plugin.Sdk.Ui;
using Calcpad.Plugins.CalculatorKit;

namespace Calcpad.Plugin.History;

/// <summary>
/// The persisted calculation history as a canvas widget: newest first, searchable, with the iPad
/// gestures - tap to send an entry back to the calculator, long press for copy expression / copy
/// result, swipe sideways to delete one entry, and an Edit mode for deleting several at once.
/// </summary>
public sealed class HistoryWidget : IWidgetRenderer
{
    private static readonly Typeface Face =
        new(new FontFamily("Inter, Segoe UI, Noto Sans CJK SC, sans-serif"));

    private static readonly Typeface BoldFace =
        new(new FontFamily("Inter, Segoe UI, Noto Sans CJK SC, sans-serif"), FontStyle.Normal, FontWeight.SemiBold);

    private readonly HistoryStore _store;
    private readonly List<(WidgetObject Target, IWidgetHost Host, EventHandler Handler)> _subscriptions = [];
    private readonly Dictionary<string, int> _pressRow = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _pressX = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _pressAt = new(StringComparer.Ordinal);

    /// <summary>Creates the widget over a history store.</summary>
    public HistoryWidget(HistoryStore store) => _store = store;

    /// <summary>
    /// Copies text to the clipboard. Installed by the plugin, which holds the permission gated
    /// clipboard API; when it is missing the widget says so instead of claiming success.
    /// </summary>
    public ClipboardWriter? Clipboard { get; set; }

    /// <summary>Command that sends an entry back to the calculator.</summary>
    public const string RefillCommandId = CalculatorCommands.RefillCommandId;

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

    /// <summary>Geometry of the list for a widget at a given size; also used by verification.</summary>
    public HistoryLayout LayoutFor(WidgetObject target, CSize size)
    {
        ArgumentNullException.ThrowIfNull(target);
        var state = target.State ?? new JsonObject();
        var entries = _store.Search(ReadString(state, "query"), 200);
        return HistoryLayout.Compute(
            size,
            entries.Count,
            ReadBool(state, "showSearch"),
            ReadInt(state, "selected", -1),
            ReadInt(state, "scroll", 0),
            ReadBool(state, "editMode"),
            ReadInt(state, "menuIndex", -1));
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
        state["editMode"] ??= false;
        state["menuIndex"] ??= -1;
        state["checked"] ??= new JsonArray();
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
        var editMode = ReadBool(state, "editMode");
        var menuIndex = ReadInt(state, "menuIndex", -1);
        var checkedKeys = ReadKeys(state, "checked");
        var entries = _store.Search(query, 200);
        var layout = HistoryLayout.Compute(size, entries.Count, showSearch, selected, scroll, editMode, menuIndex);
        var pressed = ReadString(state, "pressed");

        drawing.DrawRectangle(theme.SurfaceBrush, null, new RoundedRect(new Rect(0, 0, size.Width, size.Height), 20));
        DrawText(drawing, "历史记录 / History", layout.Title, 16, theme.Brush(theme.TextPrimary), Align.Left, true);
        DrawText(
            drawing,
            editMode ? $"{checkedKeys.Count} 已选 / selected" : $"{entries.Count} 条 / items",
            new CRect(layout.Title.X, layout.Title.Y, Math.Max(10, layout.EditButton.X - layout.Title.X - 6), layout.Title.Height),
            11,
            theme.Brush(theme.TextSecondary),
            Align.Right);

        var editFill = editMode
            ? theme.Accent
            : string.Equals(pressed, HistoryLayout.ActionEdit, StringComparison.Ordinal) ? Lighten(theme.SurfaceRaised, 0.35) : theme.SurfaceRaised;
        Panel(drawing, layout.EditButton, theme.Brush(editFill), 8);
        DrawText(
            drawing,
            editMode ? "完成" : "编辑",
            layout.EditButton,
            11,
            theme.Brush(editMode ? theme.AccentText : theme.TextPrimary),
            Align.Center,
            true);

        var toggleFill = showSearch
            ? theme.Accent
            : string.Equals(pressed, HistoryLayout.ActionSearch, StringComparison.Ordinal) ? Lighten(theme.SurfaceRaised, 0.35) : theme.SurfaceRaised;
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
            var isChecked = checkedKeys.Contains(KeyOf(entry));
            Panel(drawing, row.Row, theme.Brush(isChecked || isSelected ? theme.SurfaceRaised : theme.SurfaceAlt), 10);

            if (editMode)
            {
                Panel(drawing, row.Checkbox, theme.Brush(isChecked ? theme.Accent : theme.SurfaceRaised), 6);
                if (isChecked)
                {
                    DrawText(drawing, "✓", row.Checkbox, 12, theme.Brush(theme.AccentText), Align.Center, true);
                }
            }

            DrawFitted(drawing, entry.Expression, row.ExpressionArea, 12.5, 9, theme.Brush(theme.TextPrimary), Align.Left, isSelected);
            DrawFitted(
                drawing,
                entry.IsError ? "⚠ " + (entry.Error ?? "Error") : "= " + entry.Result,
                row.ResultArea,
                11.5,
                8,
                theme.Brush(entry.IsError ? theme.Danger : theme.Accent),
                Align.Left);

            if (!editMode)
            {
                var reinsertFill = string.Equals(pressed, $"reinsert:{row.Index}", StringComparison.Ordinal)
                    ? Lighten(theme.SurfaceRaised, 0.35)
                    : theme.SurfaceRaised;
                Panel(drawing, row.ReinsertButton, theme.Brush(reinsertFill), 8);
                DrawText(drawing, "插入", row.ReinsertButton, 11, theme.Brush(theme.TextPrimary), Align.Center, true);
            }
        }

        foreach (var button in layout.Footer)
        {
            var isDanger = button.Action is HistoryLayout.ActionClear or HistoryLayout.ActionDeleteAll or HistoryLayout.ActionDeleteSelected;
            var isPrimary = button.Action == HistoryLayout.ActionDone;
            var fill = isDanger ? theme.Danger : isPrimary ? theme.Accent : theme.SurfaceRaised;
            var textColor = isDanger || isPrimary ? Color.Parse("#FFFFFFFF") : theme.TextPrimary;
            DrawButton(drawing, theme, button.Rect, button.Label, fill, textColor, string.Equals(pressed, button.Action, StringComparison.Ordinal));
        }

        if (layout.RowMenu.Height > 0)
        {
            drawing.DrawRectangle(
                theme.Brush(Color.Parse("#F01C1C1E")),
                new Pen(theme.Brush(theme.SurfaceRaised), 1),
                new RoundedRect(new Rect(layout.RowMenu.X, layout.RowMenu.Y, layout.RowMenu.Width, layout.RowMenu.Height), 12));
            foreach (var button in layout.MenuButtons)
            {
                var isDanger = button.Action == HistoryLayout.ActionDeleteRow;
                Panel(drawing, button.Rect, theme.Brush(isDanger ? theme.Danger : theme.SurfaceRaised), 8);
                DrawText(
                    drawing,
                    button.Label,
                    button.Rect,
                    11.5,
                    theme.Brush(isDanger ? Color.Parse("#FFFFFFFF") : theme.TextPrimary),
                    Align.Left,
                    true);
            }
        }

        if (ReadString(state, "hint") is { Length: > 0 } hint)
        {
            DrawText(
                drawing,
                hint,
                new CRect(layout.Title.X, layout.Footer[0].Rect.Y - 16, layout.Title.Width, 14),
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
        var state = context.Object.State ?? new JsonObject();
        var id = context.Object.Id ?? string.Empty;
        var query = ReadString(state, "query");
        var selected = ReadInt(state, "selected", -1);
        var scroll = ReadInt(state, "scroll", 0);
        var showSearch = ReadBool(state, "showSearch");
        var editMode = ReadBool(state, "editMode");
        var menuIndex = ReadInt(state, "menuIndex", -1);
        var entries = _store.Search(query, 200);
        var layout = HistoryLayout.Compute(size, entries.Count, showSearch, selected, scroll, editMode, menuIndex);

        switch (context.Phase)
        {
            case WidgetPointerPhase.Down:
            {
                if (layout.RowMenu.Height > 0 && !layout.IsInRowMenu(context.Position))
                {
                    SetInt(context.Object, "menuIndex", -1);
                    context.Host.Invalidate();
                    return true;
                }

                var action = Hit(layout, context.Position);
                if (action is null)
                {
                    return false;
                }

                SetString(context.Object, "pressed", action);
                if (RowOf(action) is { } row)
                {
                    _pressRow[id] = row;
                    _pressX[id] = context.Position.X;
                    _pressAt[id] = Environment.TickCount64 / 1000.0;
                }

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
                var released = Hit(layout, context.Position);
                var same = string.Equals(released, action, StringComparison.Ordinal);

                if (_pressRow.TryGetValue(id, out var row))
                {
                    var dx = context.Position.X - (_pressX.TryGetValue(id, out var startedX) ? startedX : context.Position.X);
                    var held = _pressAt.TryGetValue(id, out var startedAt) ? (Environment.TickCount64 / 1000.0) - startedAt : 0;
                    _pressRow.Remove(id);
                    _pressX.Remove(id);
                    _pressAt.Remove(id);

                    if (row >= 0 && row < entries.Count && Math.Abs(dx) >= SwipeThreshold)
                    {
                        _ = DeleteEntriesAsync(context, [entries[row]], "已滑动删除 / swiped away");
                        return true;
                    }

                    if (row >= 0 && row < entries.Count && held >= LongPressSeconds)
                    {
                        SetInt(context.Object, "menuIndex", row);
                        SetInt(context.Object, "selected", row);
                        SetString(context.Object, "hint", null);
                        context.Host.Invalidate();
                        return true;
                    }
                }

                if (same)
                {
                    Perform(context, entries, layout, action);
                }

                context.Host.Invalidate();
                return true;
            }

            case WidgetPointerPhase.Cancel:
                SetString(context.Object, "pressed", null);
                _pressRow.Remove(id);
                _pressX.Remove(id);
                _pressAt.Remove(id);
                context.Host.Invalidate();
                return true;

            case WidgetPointerPhase.Wheel:
                if (layout.RowMenu.Height > 0 || entries.Count == 0)
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
                if (ReadBool(state, "editMode"))
                {
                    SetBool(context.Object, "editMode", false);
                    SetKeys(context.Object, []);
                }
                else if (ReadInt(state, "menuIndex", -1) >= 0)
                {
                    SetInt(context.Object, "menuIndex", -1);
                }
                else
                {
                    SetString(context.Object, "query", string.Empty);
                    SetBool(context.Object, "showSearch", false);
                }

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
                    if (ReadBool(state, "editMode"))
                    {
                        ToggleChecked(context.Object, KeyOf(entries[selected]));
                    }
                    else
                    {
                        _ = RefillAsync(context, entries[selected]);
                    }
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
        if (layout.RowMenu.Height > 0 && layout.RowMenu.Contains(point))
        {
            foreach (var button in layout.MenuButtons)
            {
                if (button.Rect.Contains(point))
                {
                    return $"menu:{button.Action}";
                }
            }

            return "menu:";
        }

        if (layout.EditButton.Contains(point))
        {
            return HistoryLayout.ActionEdit;
        }

        if (layout.SearchToggle.Contains(point) || (layout.SearchField.Height > 1 && layout.SearchField.Contains(point)))
        {
            return HistoryLayout.ActionSearch;
        }

        foreach (var button in layout.Footer)
        {
            if (button.Rect.Contains(point))
            {
                return $"footer:{button.Action}";
            }
        }

        foreach (var row in layout.Rows)
        {
            if (!layout.EditMode && row.ReinsertButton.Contains(point))
            {
                return $"reinsert:{row.Index}";
            }

            if (row.Row.Contains(point))
            {
                return layout.EditMode ? $"check:{row.Index}" : $"row:{row.Index}";
            }
        }

        return null;
    }

    private void Perform(WidgetPointerContext context, IReadOnlyList<HistoryEntry> entries, HistoryLayout layout, string action)
    {
        if (RowOf(action) is { } rowIndex && rowIndex >= 0 && rowIndex < entries.Count)
        {
            if (action.StartsWith("check:", StringComparison.Ordinal))
            {
                ToggleChecked(context.Object, KeyOf(entries[rowIndex]));
                SetInt(context.Object, "selected", rowIndex);
                return;
            }

            if (action.StartsWith("reinsert:", StringComparison.Ordinal))
            {
                _ = ReinsertAsync(context, entries[rowIndex]);
                return;
            }

            // A plain tap sends the calculation back to the calculator, like tapping a history row
            // on the iPad reopens it in the calculator.
            SetInt(context.Object, "selected", rowIndex);
            _ = RefillAsync(context, entries[rowIndex]);
            return;
        }

        if (action.StartsWith("menu:", StringComparison.Ordinal))
        {
            var menuAction = action.Length > 5 ? action[5..] : string.Empty;
            var menuIndex = ReadInt(context.Object.State, "menuIndex", -1);
            var entry = menuIndex >= 0 && menuIndex < entries.Count ? entries[menuIndex] : null;
            switch (menuAction)
            {
                case HistoryLayout.ActionCopyExpression:
                    if (entry is not null)
                    {
                        _ = CopyAsync(context, entry.Expression);
                    }

                    break;
                case HistoryLayout.ActionCopyResult:
                    if (entry is not null)
                    {
                        _ = CopyAsync(context, entry.IsError ? entry.Error ?? string.Empty : entry.Result);
                    }

                    break;
                case HistoryLayout.ActionDeleteRow:
                    SetInt(context.Object, "menuIndex", -1);
                    if (entry is not null)
                    {
                        _ = DeleteEntriesAsync(context, [entry], "已删除 1 条 / deleted 1");
                    }

                    break;
                default:
                    SetInt(context.Object, "menuIndex", -1);
                    break;
            }

            return;
        }

        if (action.StartsWith("footer:", StringComparison.Ordinal))
        {
            switch (action[7..])
            {
                case HistoryLayout.ActionRefresh:
                    _ = RefreshAsync(context.Host);
                    break;
                case HistoryLayout.ActionClear:
                case HistoryLayout.ActionDeleteAll:
                    _ = ConfirmAndClearAsync(context);
                    break;
                case HistoryLayout.ActionDeleteSelected:
                    _ = DeleteSelectedAsync(context, entries);
                    break;
                case HistoryLayout.ActionDone:
                    SetBool(context.Object, "editMode", false);
                    SetKeys(context.Object, []);
                    break;
            }

            return;
        }

        switch (action)
        {
            case HistoryLayout.ActionSearch:
                SetBool(context.Object, "showSearch", !ReadBool(context.Object.State, "showSearch"));
                break;
            case HistoryLayout.ActionEdit:
                var editMode = !ReadBool(context.Object.State, "editMode");
                SetBool(context.Object, "editMode", editMode);
                SetKeys(context.Object, []);
                SetInt(context.Object, "menuIndex", -1);
                break;
        }

        _ = layout;
    }

    /// <summary>Reads the row index out of a row, check or reinsert action, or null.</summary>
    private static int? RowOf(string action)
    {
        foreach (var prefix in Prefixes)
        {
            if (action.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(action[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                return index;
            }
        }

        return null;
    }

    private static readonly string[] Prefixes = ["row:", "check:", "reinsert:"];

    /// <summary>Horizontal travel that counts as a swipe, in logical units.</summary>
    public const double SwipeThreshold = 40;

    /// <summary>Hold time that opens the row menu instead of refilling the calculator.</summary>
    public const double LongPressSeconds = 0.5;

    /// <summary>Stable identity of an entry, used by the multi select checkboxes.</summary>
    internal static string KeyOf(HistoryEntry entry) => HistoryStore.KeyOf(entry);

    private static HashSet<string> ReadKeys(JsonObject? state, string key)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (state is not null && state.TryGetPropertyValue(key, out var node) && node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item?.ToString() is { Length: > 0 } text)
                {
                    set.Add(text);
                }
            }
        }

        return set;
    }

    private static void SetKeys(WidgetObject target, IEnumerable<string> keys)
    {
        target.State ??= new JsonObject();
        var array = new JsonArray();
        foreach (var key in keys)
        {
            array.Add(key);
        }

        target.State["checked"] = array;
    }

    private static void ToggleChecked(WidgetObject target, string key)
    {
        var state = target.State ??= new JsonObject();
        var keys = ReadKeys(state, "checked");
        if (!keys.Add(key))
        {
            keys.Remove(key);
        }

        SetKeys(target, keys);
    }

    /// <summary>Copies text through the plugin's permission gated clipboard channel.</summary>
    private async Task CopyAsync(WidgetPointerContext context, string text)
    {
        var outcome = Clipboard is null
            ? ClipboardOutcome.Unavailable
            : await Clipboard(text, CancellationToken.None).ConfigureAwait(true);
        SetString(context.Object, "hint", CalculatorClipboard.Describe(outcome));
        SetInt(context.Object, "menuIndex", -1);
        context.Host.Invalidate();
    }

    /// <summary>Deletes a set of entries, from the cache and from the database.</summary>
    private async Task DeleteEntriesAsync(WidgetPointerContext context, IReadOnlyList<HistoryEntry> entries, string message)
    {
        if (entries.Count == 0)
        {
            return;
        }

        try
        {
            await _store.RemoveAsync(entries).ConfigureAwait(true);
            SetInt(context.Object, "selected", -1);
            SetInt(context.Object, "menuIndex", -1);
            SetInt(context.Object, "scroll", 0);
            SetKeys(context.Object, []);
            SetString(context.Object, "hint", message);
            await context.Host.Host.DispatchAsync(context.Host.Invalidate).ConfigureAwait(true);
        }
        catch (Exception)
        {
            SetString(context.Object, "hint", "删除失败 / delete failed");
            context.Host.Invalidate();
        }
    }

    private async Task DeleteSelectedAsync(WidgetPointerContext context, IReadOnlyList<HistoryEntry> entries)
    {
        var keys = ReadKeys(context.Object.State, "checked");
        if (keys.Count == 0)
        {
            SetString(context.Object, "hint", "未选择 / nothing selected");
            context.Host.Invalidate();
            return;
        }

        var victims = entries.Where(e => keys.Contains(KeyOf(e))).ToList();
        await DeleteEntriesAsync(context, victims, $"已删除 {victims.Count} 条 / deleted {victims.Count}").ConfigureAwait(true);
    }

    /// <summary>Sends an entry back to the calculator through the cross plugin refill command.</summary>
    private async Task RefillAsync(WidgetPointerContext context, HistoryEntry entry)
    {
        try
        {
            var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CalculatorCommands.ExpressionArgument] = entry.Expression,
                [CalculatorCommands.ResultArgument] = entry.Result ?? string.Empty
            };
            await context.Host.InvokeCommandAsync(RefillCommandId, arguments).ConfigureAwait(true);
            SetString(context.Object, "hint", "已回填到计算器 / sent to calculator");
        }
        catch (Exception)
        {
            SetString(context.Object, "hint", "回填失败 / refill failed");
        }

        context.Host.Invalidate();
    }

    private async Task RefillAsync(WidgetKeyContext context, HistoryEntry entry)
    {
        try
        {
            var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CalculatorCommands.ExpressionArgument] = entry.Expression,
                [CalculatorCommands.ResultArgument] = entry.Result ?? string.Empty
            };
            await context.Host.InvokeCommandAsync(RefillCommandId, arguments).ConfigureAwait(true);
            SetString(context.Object, "hint", "已回填到计算器 / sent to calculator");
        }
        catch (Exception)
        {
            SetString(context.Object, "hint", "回填失败 / refill failed");
        }

        context.Host.Invalidate();
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
