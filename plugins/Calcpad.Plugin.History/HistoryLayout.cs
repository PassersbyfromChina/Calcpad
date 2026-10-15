using Calcpad.Core.Canvas;

namespace Calcpad.Plugin.History;

/// <summary>Geometry of one history row.</summary>
public sealed record HistoryRowLayout(
    int Index,
    CRect Row,
    CRect ExpressionArea,
    CRect ResultArea,
    CRect ReinsertButton,
    CRect Checkbox);

/// <summary>One footer or menu button.</summary>
public sealed record HistoryButton(string Action, string Label, CRect Rect);

/// <summary>
/// Pure geometry of the history widget: the list, the search field, the Edit toggle, the footer
/// (which becomes Delete selected / Delete all / Done in edit mode) and the row context menu that a
/// long press opens.
/// </summary>
public sealed class HistoryLayout
{
    /// <summary>Title strip.</summary>
    public CRect Title { get; init; }

    /// <summary>Edit toggle in the title row.</summary>
    public CRect EditButton { get; init; }

    /// <summary>Search toggle in the title row.</summary>
    public CRect SearchToggle { get; init; }

    /// <summary>Search field; empty when search is collapsed.</summary>
    public CRect SearchField { get; init; }

    /// <summary>Area the rows are drawn into.</summary>
    public CRect ListArea { get; init; }

    /// <summary>Visible rows.</summary>
    public IReadOnlyList<HistoryRowLayout> Rows { get; init; } = [];

    /// <summary>Placeholder area shown when there is nothing to list.</summary>
    public CRect EmptyState { get; init; }

    /// <summary>Footer buttons, left to right.</summary>
    public IReadOnlyList<HistoryButton> Footer { get; init; } = [];

    /// <summary>Row context menu panel; empty when closed.</summary>
    public CRect RowMenu { get; init; }

    /// <summary>Buttons inside the row context menu.</summary>
    public IReadOnlyList<HistoryButton> MenuButtons { get; init; } = [];

    /// <summary>True while multi select is on.</summary>
    public bool EditMode { get; init; }

    /// <summary>First entry index that is visible.</summary>
    public int FirstVisible { get; init; }

    /// <summary>Action id of the clear button.</summary>
    public const string ActionClear = "clear";

    /// <summary>Action id of the refresh button.</summary>
    public const string ActionRefresh = "refresh";

    /// <summary>Action id of the Edit toggle.</summary>
    public const string ActionEdit = "edit";

    /// <summary>Action id of the search toggle.</summary>
    public const string ActionSearch = "search";

    /// <summary>Action id of "delete the selected entries".</summary>
    public const string ActionDeleteSelected = "deleteSelected";

    /// <summary>Action id of "delete everything".</summary>
    public const string ActionDeleteAll = "deleteAll";

    /// <summary>Action id of "leave edit mode".</summary>
    public const string ActionDone = "done";

    /// <summary>Action id of "copy the expression of the menu's row".</summary>
    public const string ActionCopyExpression = "copyExpression";

    /// <summary>Action id of "copy the result of the menu's row".</summary>
    public const string ActionCopyResult = "copyResult";

    /// <summary>Action id of "delete the row the menu belongs to".</summary>
    public const string ActionDeleteRow = "deleteRow";

    /// <summary>Action id of "close the menu".</summary>
    public const string ActionCancelMenu = "cancelMenu";

    /// <summary>Computes the layout for a widget of the given logical size.</summary>
    public static HistoryLayout Compute(
        CSize size,
        int itemCount,
        bool showSearch,
        int selected,
        int scroll,
        bool editMode,
        int menuIndex)
    {
        const double padding = 12;
        var width = Math.Max(220, size.Width);
        var height = Math.Max(180, size.Height);
        var title = new CRect(padding, 8, width - (padding * 2), 28);

        // Edit toggle on the left of the search toggle, both right aligned in the title row.
        var editButton = new CRect(title.Right - 118, title.Y + 3, 54, 22);
        var searchToggle = new CRect(title.Right - 58, title.Y + 3, 58, 22);

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
            var checkboxWidth = editMode ? 26d : 0d;
            var buttonWidth = Math.Min(54, row.Width * 0.2);
            var textLeft = row.X + 8 + checkboxWidth;
            var textWidth = Math.Max(20, row.Right - textLeft - buttonWidth - 12);
            rows.Add(new HistoryRowLayout(
                i,
                row,
                new CRect(textLeft, row.Y + 2, textWidth, row.Height * 0.5),
                new CRect(textLeft, row.Y + (row.Height * 0.5), textWidth, (row.Height * 0.5) - 2),
                new CRect(row.Right - buttonWidth - 6, row.Y + ((row.Height - 26) / 2), buttonWidth, 26),
                new CRect(row.X + 6, row.Y + ((row.Height - 20) / 2), 20, 20)));
        }

        var footer = new List<HistoryButton>();
        var gap = 8d;
        if (editMode)
        {
            var buttonWidth = (title.Width - (gap * 2)) / 3;
            footer.Add(new HistoryButton(ActionDeleteSelected, "删除所选 / Delete", new CRect(title.X, footerTop, buttonWidth, footerHeight)));
            footer.Add(new HistoryButton(ActionDeleteAll, "全部清除 / All", new CRect(title.X + buttonWidth + gap, footerTop, buttonWidth, footerHeight)));
            footer.Add(new HistoryButton(ActionDone, "完成 / Done", new CRect(title.X + ((buttonWidth + gap) * 2), footerTop, buttonWidth, footerHeight)));
        }
        else
        {
            var buttonWidth = (title.Width - gap) / 2;
            footer.Add(new HistoryButton(ActionClear, "清空 / Clear", new CRect(title.X, footerTop, buttonWidth, footerHeight)));
            footer.Add(new HistoryButton(ActionRefresh, "刷新 / Refresh", new CRect(title.X + buttonWidth + gap, footerTop, buttonWidth, footerHeight)));
        }

        var rowMenu = CRect.Empty;
        var menuButtons = new List<HistoryButton>();
        if (menuIndex >= 0 && menuIndex < itemCount)
        {
            const double itemHeight = 32;
            var menuWidth = Math.Min(200, width - 24);
            var menuHeight = itemHeight * 4;
            var target = rows.FirstOrDefault(r => r.Index == menuIndex);
            var top = target is not null ? target.Row.Y + target.Row.Height : listArea.Y;
            var menuTop = Math.Clamp(top - menuHeight, listArea.Y, Math.Max(listArea.Y, listArea.Bottom - menuHeight));
            rowMenu = new CRect(listArea.X + 4, menuTop, menuWidth, menuHeight);
            menuButtons.Add(new HistoryButton(ActionCopyExpression, "复制表达式 / Copy expression", new CRect(rowMenu.X + 4, rowMenu.Y + 2, rowMenu.Width - 8, itemHeight - 4)));
            menuButtons.Add(new HistoryButton(ActionCopyResult, "复制结果 / Copy result", new CRect(rowMenu.X + 4, rowMenu.Y + itemHeight + 2, rowMenu.Width - 8, itemHeight - 4)));
            menuButtons.Add(new HistoryButton(ActionDeleteRow, "删除该条 / Delete", new CRect(rowMenu.X + 4, rowMenu.Y + (itemHeight * 2) + 2, rowMenu.Width - 8, itemHeight - 4)));
            menuButtons.Add(new HistoryButton(ActionCancelMenu, "取消 / Cancel", new CRect(rowMenu.X + 4, rowMenu.Y + (itemHeight * 3) + 2, rowMenu.Width - 8, itemHeight - 4)));
        }

        return new HistoryLayout
        {
            Title = title,
            EditButton = editButton,
            SearchToggle = searchToggle,
            SearchField = searchField,
            ListArea = listArea,
            Rows = rows,
            EmptyState = listArea,
            Footer = footer,
            RowMenu = rowMenu,
            MenuButtons = menuButtons,
            EditMode = editMode,
            FirstVisible = first
        };
    }

    /// <summary>True when the point is inside the open row menu.</summary>
    public bool IsInRowMenu(CPoint point) => RowMenu.Height > 0 && RowMenu.Contains(point);
}
