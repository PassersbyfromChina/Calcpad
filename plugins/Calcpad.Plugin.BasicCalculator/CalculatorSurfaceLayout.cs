using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;

namespace Calcpad.Plugins.CalculatorKit;

/// <summary>What a pointer press means on the calculator surface.</summary>
public enum CalculatorSurfaceActionKind
{
    /// <summary>Nothing at this point.</summary>
    None,

    /// <summary>A keypad key; <see cref="CalculatorSurfaceAction.Id"/> is the key id.</summary>
    Key,

    /// <summary>Switch mode; <see cref="CalculatorSurfaceAction.Mode"/> is the target.</summary>
    SetMode,

    /// <summary>Open or close the mode popup (the calculator icon in the bottom left corner).</summary>
    ToggleModeMenu,

    /// <summary>Open the history list (the three lines in the top left corner).</summary>
    OpenHistory,

    /// <summary>MC.</summary>
    MemoryClear,

    /// <summary>M+.</summary>
    MemoryAdd,

    /// <summary>M−.</summary>
    MemorySubtract,

    /// <summary>MR.</summary>
    MemoryRecall,

    /// <summary>Pull the last expression back into the entry by tapping the result.</summary>
    RecallResult,

    /// <summary>Exchange the active and the secondary unit.</summary>
    SwapUnits,

    /// <summary>Open the picker for the active unit.</summary>
    OpenPrimaryPicker,

    /// <summary>Open the picker for the secondary unit.</summary>
    OpenSecondaryPicker,

    /// <summary>Open the category picker.</summary>
    OpenCategoryPicker,

    /// <summary>Close the picker.</summary>
    ClosePicker,

    /// <summary>Choose the active unit; the id is the unit symbol.</summary>
    PickPrimaryUnit,

    /// <summary>Choose the secondary unit; the id is the unit symbol.</summary>
    PickSecondaryUnit,

    /// <summary>Choose the category; the id is the category id.</summary>
    PickCategory,

    /// <summary>Scroll the picker.</summary>
    ScrollPicker,

    /// <summary>Switch the currency source between manual and daily.</summary>
    ToggleRates,

    /// <summary>Fetch daily rates now.</summary>
    RefreshRates,

    /// <summary>Open the panel where manual rates are edited.</summary>
    OpenRatesEditor,

    /// <summary>Show the next page of a paged keypad.</summary>
    NextPage
}

/// <summary>One actionable region of the surface.</summary>
public sealed record CalculatorSurfaceAction(
    CalculatorSurfaceActionKind Kind,
    string? Id = null,
    CalculatorMode Mode = CalculatorMode.Basic);

/// <summary>Everything the layout needs to know about the current frame.</summary>
public sealed record CalculatorSurfaceRequest(
    CalculatorMode Mode,
    bool ShowModeMenu,
    string Picker,
    bool ShowMemory,
    bool CanRecallResult,
    int PageIndex,
    int PageCount,
    double Gap,
    double Padding,
    IReadOnlyList<UnitCategory> Categories,
    string CategoryId,
    int PickerScroll,
    string SourceLabel);

/// <summary>
/// Pure geometry of the calculator surface. Nothing here touches Avalonia, so the whole chrome -
/// mode chip in the bottom left, history icon in the top left, memory row, the two unit lines and
/// the pickers - can be verified without a screen.
/// </summary>
public sealed class CalculatorSurfaceLayout
{
    /// <summary>Height of the top and bottom chrome strips.</summary>
    public const double ChromeHeight = 26;

    /// <summary>Height of the memory row.</summary>
    public const double MemoryRowHeight = 24;

    /// <summary>Height of the convert category/source row.</summary>
    public const double ConvertRowHeight = 24;

    /// <summary>Outer panel.</summary>
    public CRect Panel { get; init; }

    /// <summary>Three line history button, top left.</summary>
    public CRect HistoryButton { get; init; }

    /// <summary>Top strip, right of the history button.</summary>
    public CRect TopStatus { get; init; }

    /// <summary>Whole display area.</summary>
    public CRect DisplayArea { get; init; }

    /// <summary>Small expression line.</summary>
    public CRect ExpressionArea { get; init; }

    /// <summary>Large value line.</summary>
    public CRect ValueArea { get; init; }

    /// <summary>Bright line holding the value and the active unit (convert mode).</summary>
    public CRect ConvertPrimaryRow { get; init; }

    /// <summary>Dim line holding the converted value and the secondary unit.</summary>
    public CRect ConvertSecondaryRow { get; init; }

    /// <summary>Swap button between the two unit lines.</summary>
    public CRect ConvertSwapButton { get; init; }

    /// <summary>Category button.</summary>
    public CRect ConvertCategoryButton { get; init; }

    /// <summary>Source label area (manual rate / daily rate).</summary>
    public CRect ConvertSourceLabel { get; init; }

    /// <summary>Row holding MC, M+, M− and MR.</summary>
    public CRect MemoryRow { get; init; }

    /// <summary>MC chip.</summary>
    public CRect MemoryClear { get; init; }

    /// <summary>M+ chip.</summary>
    public CRect MemoryAdd { get; init; }

    /// <summary>M− chip.</summary>
    public CRect MemorySubtract { get; init; }

    /// <summary>MR chip; highlighted while the memory holds a value.</summary>
    public CRect MemoryRecall { get; init; }

    /// <summary>Memory indicator text area.</summary>
    public CRect MemoryIndicator { get; init; }

    /// <summary>Area the keypad is laid out in.</summary>
    public CRect KeypadArea { get; init; }

    /// <summary>Area the delegated Math Notes page occupies.</summary>
    public CRect NoteArea { get; init; }

    /// <summary>Bottom strip.</summary>
    public CRect BottomChrome { get; init; }

    /// <summary>Calculator icon button in the bottom left corner.</summary>
    public CRect ModeButton { get; init; }

    /// <summary>Page toggle of a paged keypad.</summary>
    public CRect PageButton { get; init; }

    /// <summary>Hint text area.</summary>
    public CRect HintArea { get; init; }

    /// <summary>Picker overlay; empty when closed.</summary>
    public CRect PickerArea { get; init; }

    /// <summary>Picker close button.</summary>
    public CRect PickerCloseButton { get; init; }

    /// <summary>Currency source toggle inside the picker.</summary>
    public CRect PickerRatesToggle { get; init; }

    /// <summary>Currency refresh button inside the picker.</summary>
    public CRect PickerRatesRefresh { get; init; }

    /// <summary>Button that opens the panel where manual rates are edited.</summary>
    public CRect PickerRatesEdit { get; init; }

    /// <summary>Mode popup entries, drawn above the mode button.</summary>
    public IReadOnlyList<(CalculatorMode Mode, CRect Rect)> ModeItems { get; init; } = [];

    /// <summary>Unit entries of the open picker.</summary>
    public IReadOnlyList<(UnitInfo Unit, CRect Rect)> PickerUnits { get; init; } = [];

    /// <summary>Category entries of the open picker.</summary>
    public IReadOnlyList<(UnitCategory Category, CRect Rect)> PickerCategories { get; init; } = [];

    /// <summary>Keys of the active keypad, already positioned by <see cref="Compute"/>.</summary>
    public IReadOnlyList<(CalculatorKey Key, CRect Rect)> Keys { get; init; } = [];

    /// <summary>Rows and columns of the active keypad.</summary>
    public int Rows { get; init; }

    /// <summary>Columns of the active keypad.</summary>
    public int Columns { get; init; }

    /// <summary>True when the keypad is drawn with round keys.</summary>
    public bool RoundKeys { get; init; }

    /// <summary>True when the picker scrolls and more units exist below.</summary>
    public bool PickerHasMore { get; init; }

    /// <summary>Computes the layout for a widget of the given logical size.</summary>
    public static CalculatorSurfaceLayout Compute(CSize size, CalculatorSurfaceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var width = Math.Max(300, size.Width);
        var height = Math.Max(280, size.Height);
        var padding = Math.Clamp(request.Padding, 6, 16);
        var gap = Math.Clamp(request.Gap, 3, 12);
        var panel = new CRect(0, 0, width, height);

        var historyButton = new CRect(padding, padding, 26, 22);
        var topStatus = new CRect(historyButton.Right + 6, padding, Math.Max(10, width - historyButton.Right - (padding * 2) - 6), 22);

        var convert = request.Mode == CalculatorMode.Convert;
        var notes = request.Mode == CalculatorMode.MathNotes;

        // In math notes mode the note page takes the whole surface below the top chrome, so the
        // calculator display does not sit above the page.
        var displayHeight = notes
            ? 0
            : convert
                ? Math.Clamp(height * 0.27, 104, 190)
                : Math.Clamp(height * 0.2, 62, 150);
        var displayArea = notes
            ? CRect.Empty
            : new CRect(padding, topStatus.Bottom + 4, width - (padding * 2), displayHeight);
        var expressionHeight = Math.Max(14, displayArea.Height * (convert ? 0.26 : 0.34));
        var expressionArea = notes
            ? CRect.Empty
            : new CRect(displayArea.X + 4, displayArea.Y + 2, Math.Max(10, displayArea.Width - 8), expressionHeight);
        var valueArea = notes
            ? CRect.Empty
            : new CRect(displayArea.X + 4, expressionArea.Bottom, Math.Max(10, displayArea.Width - 8), Math.Max(10, displayArea.Height - expressionHeight - 4));

        var convertPrimary = CRect.Empty;
        var convertSecondary = CRect.Empty;
        var swap = CRect.Empty;
        if (convert)
        {
            var linesHeight = Math.Max(30, valueArea.Height);
            var primaryHeight = linesHeight * 0.56;
            convertPrimary = new CRect(valueArea.X, valueArea.Y, valueArea.Width, primaryHeight);
            convertSecondary = new CRect(valueArea.X, convertPrimary.Bottom, valueArea.Width, Math.Max(14, linesHeight - primaryHeight));
            var swapSize = Math.Min(30, Math.Max(20, convertSecondary.Height - 4));
            swap = new CRect(displayArea.Right - swapSize - 4, convertSecondary.Y + Math.Max(0, (convertSecondary.Height - swapSize) / 2), swapSize, swapSize);
        }

        var memoryRow = request.ShowMemory
            ? new CRect(padding, displayArea.Bottom + 4, width - (padding * 2), MemoryRowHeight)
            : CRect.Empty;
        var chipWidth = 46d;
        var chipGap = 6d;
        var chipsY = memoryRow.Y + ((MemoryRowHeight - 20) / 2);
        var recall = memoryRow.Height > 0 ? new CRect(memoryRow.Right - chipWidth, chipsY, chipWidth, 20) : CRect.Empty;
        var memoryAdd = memoryRow.Height > 0 ? new CRect(recall.X - chipGap - chipWidth, chipsY, chipWidth, 20) : CRect.Empty;
        var memorySubtract = memoryRow.Height > 0 ? new CRect(memoryAdd.X - chipGap - chipWidth, chipsY, chipWidth, 20) : CRect.Empty;
        var memoryClear = memoryRow.Height > 0 ? new CRect(memorySubtract.X - chipGap - chipWidth, chipsY, chipWidth, 20) : CRect.Empty;
        var memoryIndicator = memoryRow.Height > 0
            ? new CRect(memoryRow.X, memoryRow.Y, Math.Max(10, memoryClear.X - memoryRow.X - 6), MemoryRowHeight)
            : CRect.Empty;

        var convertRow = convert
            ? new CRect(padding, memoryRow.Height > 0 ? memoryRow.Bottom + 4 : displayArea.Bottom + 4, width - (padding * 2), ConvertRowHeight)
            : CRect.Empty;
        var categoryButton = convert ? new CRect(convertRow.X, convertRow.Y, Math.Min(120, convertRow.Width * 0.4), ConvertRowHeight) : CRect.Empty;
        var sourceLabel = convert
            ? new CRect(categoryButton.Right + 6, convertRow.Y, Math.Max(10, convertRow.Right - categoryButton.Right - 6), ConvertRowHeight)
            : CRect.Empty;

        var bottomChrome = new CRect(padding, height - padding - ChromeHeight, width - (padding * 2), ChromeHeight);
        var modeButton = new CRect(bottomChrome.X, bottomChrome.Y, Math.Min(112, bottomChrome.Width * 0.42), ChromeHeight);
        var pageButton = request.PageCount > 1
            ? new CRect(modeButton.Right + 6, bottomChrome.Y, 54, ChromeHeight)
            : CRect.Empty;
        var hintArea = new CRect(pageButton.Width > 0 ? pageButton.Right + 6 : modeButton.Right + 6, bottomChrome.Y, Math.Max(10, bottomChrome.Right - (pageButton.Width > 0 ? pageButton.Right : modeButton.Right) - 6), ChromeHeight);

        var contentTop = (convert ? convertRow.Bottom : memoryRow.Height > 0 ? memoryRow.Bottom : displayArea.Bottom) + 6;
        var contentHeight = Math.Max(20, bottomChrome.Y - contentTop - 6);
        var keypadArea = notes ? CRect.Empty : new CRect(padding, contentTop, width - (padding * 2), contentHeight);
        var noteArea = notes
            ? new CRect(padding, topStatus.Bottom + 4, width - (padding * 2), Math.Max(20, bottomChrome.Y - topStatus.Bottom - 10))
            : CRect.Empty;

        var modeItems = new List<(CalculatorMode, CRect)>();
        if (request.ShowModeMenu)
        {
            var itemHeight = 30d;
            var menuWidth = Math.Min(210, width - (padding * 2));
            var menuHeight = (itemHeight * CalculatorModes.All.Count) + 8;
            var menuTop = Math.Max(padding, modeButton.Y - menuHeight - 6);
            for (var i = 0; i < CalculatorModes.All.Count; i++)
            {
                modeItems.Add((CalculatorModes.All[i], new CRect(modeButton.X, menuTop + 4 + (i * itemHeight), menuWidth, itemHeight)));
            }
        }

        var pickerArea = request.Picker.Length > 0 ? keypadArea : CRect.Empty;
        var pickerClose = CRect.Empty;
        var ratesToggle = CRect.Empty;
        var ratesRefresh = CRect.Empty;
        var ratesEdit = CRect.Empty;
        var pickerUnits = new List<(UnitInfo, CRect)>();
        var pickerCategories = new List<(UnitCategory, CRect)>();
        var pickerHasMore = false;
        if (pickerArea.Height > 1)
        {
            pickerClose = new CRect(pickerArea.Right - 28, pickerArea.Y + 6, 26, 22);
            if (string.Equals(request.CategoryId, CurrencyRateTable.CategoryId, StringComparison.OrdinalIgnoreCase))
            {
                ratesToggle = new CRect(pickerArea.X + 6, pickerArea.Y + 6, 118, 22);
                ratesRefresh = new CRect(ratesToggle.Right + 5, pickerArea.Y + 6, 56, 22);
                ratesEdit = new CRect(ratesRefresh.Right + 5, pickerArea.Y + 6, Math.Max(40, Math.Min(72, pickerClose.X - ratesRefresh.Right - 10)), 22);
            }

            var gridTop = pickerArea.Y + 34;
            var gridHeight = Math.Max(10, pickerArea.Bottom - gridTop - 6);
            const int perRow = 3;
            const double pillHeight = 28;
            const double pillGap = 5;
            if (string.Equals(request.Picker, "categories", StringComparison.OrdinalIgnoreCase))
            {
                var visibleRows = Math.Max(1, (int)((gridHeight + pillGap) / (pillHeight + pillGap)));
                var capacity = visibleRows * perRow;
                var scroll = Math.Clamp(request.PickerScroll, 0, Math.Max(0, request.Categories.Count - capacity));
                pickerHasMore = request.Categories.Count > scroll + capacity;
                for (var i = scroll; i < Math.Min(request.Categories.Count, scroll + capacity); i++)
                {
                    pickerCategories.Add((request.Categories[i], Cell(gridTop, pickerArea, i - scroll, perRow, pillHeight, pillGap)));
                }
            }
            else
            {
                var category = request.Categories.FirstOrDefault(c => string.Equals(c.Id, request.CategoryId, StringComparison.OrdinalIgnoreCase))
                               ?? request.Categories.FirstOrDefault();
                if (category is not null)
                {
                    var visibleRows = Math.Max(1, (int)((gridHeight + pillGap) / (pillHeight + pillGap)));
                    var capacity = visibleRows * perRow;
                    var scroll = Math.Clamp(request.PickerScroll, 0, Math.Max(0, category.Units.Count - capacity));
                    pickerHasMore = category.Units.Count > scroll + capacity;
                    for (var i = scroll; i < Math.Min(category.Units.Count, scroll + capacity); i++)
                    {
                        pickerUnits.Add((category.Units[i], Cell(gridTop, pickerArea, i - scroll, perRow, pillHeight, pillGap)));
                    }
                }
            }
        }

        var page = request.PageCount > 1 ? Math.Clamp(request.PageIndex, 0, request.PageCount - 1) : 0;
        var (keys, rows, columns, round) = Keypad(request.Mode, keypadArea, width, gap, page);
        var placed = new List<(CalculatorKey, CRect)>(keys.Count);
        foreach (var key in keys)
        {
            placed.Add((key, KeypadGeometry.RectIn(keypadArea, key, rows, columns, gap)));
        }

        return new CalculatorSurfaceLayout
        {
            Panel = panel,
            HistoryButton = historyButton,
            TopStatus = topStatus,
            DisplayArea = displayArea,
            ExpressionArea = expressionArea,
            ValueArea = valueArea,
            ConvertPrimaryRow = convertPrimary,
            ConvertSecondaryRow = convertSecondary,
            ConvertSwapButton = swap,
            ConvertCategoryButton = categoryButton,
            ConvertSourceLabel = sourceLabel,
            MemoryRow = memoryRow,
            MemoryClear = memoryClear,
            MemoryAdd = memoryAdd,
            MemorySubtract = memorySubtract,
            MemoryRecall = recall,
            MemoryIndicator = memoryIndicator,
            KeypadArea = keypadArea,
            NoteArea = noteArea,
            BottomChrome = bottomChrome,
            ModeButton = modeButton,
            PageButton = pageButton,
            HintArea = hintArea,
            PickerArea = pickerArea,
            PickerCloseButton = pickerClose,
            PickerRatesToggle = ratesToggle,
            PickerRatesRefresh = ratesRefresh,
            PickerRatesEdit = ratesEdit,
            ModeItems = modeItems,
            PickerUnits = pickerUnits,
            PickerCategories = pickerCategories,
            Keys = placed,
            Rows = rows,
            Columns = columns,
            RoundKeys = round,
            PickerHasMore = pickerHasMore
        };
    }

    /// <summary>Finds the action under a point. Popups win over the chrome, which wins over keys.</summary>
    public static CalculatorSurfaceAction HitTest(CalculatorSurfaceLayout layout, CPoint point)
    {
        ArgumentNullException.ThrowIfNull(layout);

        foreach (var (mode, rect) in layout.ModeItems)
        {
            if (rect.Contains(point))
            {
                return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.SetMode, null, mode);
            }
        }

        if (layout.PickerArea.Height > 1 && layout.PickerArea.Contains(point))
        {
            if (layout.PickerCloseButton.Contains(point))
            {
                return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.ClosePicker);
            }

            if (layout.PickerRatesToggle.Height > 0 && layout.PickerRatesToggle.Contains(point))
            {
                return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.ToggleRates);
            }

            if (layout.PickerRatesRefresh.Height > 0 && layout.PickerRatesRefresh.Contains(point))
            {
                return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.RefreshRates);
            }

            if (layout.PickerRatesEdit.Height > 0 && layout.PickerRatesEdit.Contains(point))
            {
                return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.OpenRatesEditor);
            }

            foreach (var (unit, rect) in layout.PickerUnits)
            {
                if (rect.Contains(point))
                {
                    return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.PickPrimaryUnit, unit.Symbol);
                }
            }

            foreach (var (category, rect) in layout.PickerCategories)
            {
                if (rect.Contains(point))
                {
                    return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.PickCategory, category.Id);
                }
            }

            return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.None);
        }

        if (layout.HistoryButton.Contains(point))
        {
            return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.OpenHistory);
        }

        if (layout.MemoryRow.Height > 0 && layout.MemoryRow.Contains(point))
        {
            if (layout.MemoryClear.Contains(point))
            {
                return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.MemoryClear);
            }

            if (layout.MemoryAdd.Contains(point))
            {
                return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.MemoryAdd);
            }

            if (layout.MemorySubtract.Contains(point))
            {
                return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.MemorySubtract);
            }

            if (layout.MemoryRecall.Contains(point))
            {
                return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.MemoryRecall);
            }
        }

        if (layout.ConvertPrimaryRow.Height > 0 && layout.ConvertPrimaryRow.Contains(point))
        {
            return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.OpenPrimaryPicker);
        }

        if (layout.ConvertSecondaryRow.Height > 0 && layout.ConvertSecondaryRow.Contains(point))
        {
            if (layout.ConvertSwapButton.Contains(point))
            {
                return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.SwapUnits);
            }

            return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.OpenSecondaryPicker);
        }

        if (layout.ConvertCategoryButton.Height > 0 && layout.ConvertCategoryButton.Contains(point))
        {
            return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.OpenCategoryPicker);
        }

        if (layout.ValueArea.Contains(point) && layout.ConvertPrimaryRow.Height <= 0)
        {
            return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.RecallResult);
        }

        if (layout.ModeButton.Contains(point))
        {
            return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.ToggleModeMenu);
        }

        if (layout.PageButton.Height > 0 && layout.PageButton.Contains(point))
        {
            return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.NextPage);
        }

        foreach (var (key, rect) in layout.Keys)
        {
            if (rect.Contains(point))
            {
                return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.Key, key.Id);
            }
        }

        return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.None);
    }

    /// <summary>True when the mode menu is drawn and this point is inside it.</summary>
    public bool IsInModeMenu(CPoint point) => ModeItems.Any(item => item.Rect.Contains(point));

    private static (IReadOnlyList<CalculatorKey> Keys, int Rows, int Columns, bool Round) Keypad(
        CalculatorMode mode,
        CRect area,
        double width,
        double gap,
        int page)
    {
        switch (mode)
        {
            case CalculatorMode.Scientific:
                if (CalculatorKeypadPages.NeedsPaging(width))
                {
                    var pages = CalculatorKeypadPages.ScientificNarrow;
                    var current = pages[Math.Clamp(page, 0, pages.Count - 1)];
                    return (current.Keys, current.Rows, current.Columns, false);
                }

                return (CalculatorKeypads.Scientific, CalculatorKeypads.ScientificRows, CalculatorKeypads.ScientificColumns, false);

            case CalculatorMode.MathNotes:
                return ([], 1, 1, false);

            default:
                return (CalculatorKeypads.Basic, CalculatorKeypads.BasicRows, CalculatorKeypads.BasicColumns, true);
        }
    }

    private static CRect Cell(double top, CRect area, int index, int perRow, double height, double gap)
    {
        var row = index / perRow;
        var column = index % perRow;
        var cellWidth = (area.Width - 12 - (gap * (perRow - 1))) / perRow;
        return new CRect(area.X + 6 + (column * (cellWidth + gap)), top + (row * (height + gap)), cellWidth, height);
    }
}

/// <summary>What the two unit lines of convert mode show.</summary>
public sealed record CalculatorConvertDisplay(
    string PrimaryText,
    string SecondaryText,
    string? Error,
    string PrimaryUnit,
    string SecondaryUnit);

/// <summary>Builds the two unit lines and enforces the honest-rate rules.</summary>
public static class CalculatorConvertFormatter
{
    /// <summary>
    /// Formats the result of an expression in the active unit, plus the same quantity in the
    /// secondary unit. The expression itself is always evaluated without a unit; the unit is applied
    /// here, which is what makes <c>1 + 2 =</c> show <c>3 km</c> with <c>1.86 mi</c> underneath.
    /// </summary>
    public static CalculatorConvertDisplay Build(
        string? resultText,
        CalculatorConvertState state,
        IUnitConverter converter,
        CurrencyRateTable rates)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(rates);

        var primaryUnit = state.PrimaryUnit;
        var value = 0.0;
        var hasValue = false;

        if (!string.IsNullOrWhiteSpace(resultText))
        {
            if (NumberFormatter.TrySplitQuantity(resultText, out var quantity, out var unit) && !string.IsNullOrWhiteSpace(unit))
            {
                value = quantity;
                hasValue = true;
                if (converter.FindCategory(state.CategoryId)?.Find(unit) is not null || rates.IsCurrency(unit))
                {
                    primaryUnit = unit;
                }
            }
            else if (double.TryParse(resultText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var plain))
            {
                value = plain;
                hasValue = true;
            }
        }

        var primary = hasValue ? $"{NumberFormatter.Format(value, 12)} {primaryUnit}".Trim() : string.Empty;
        if (!hasValue)
        {
            return new CalculatorConvertDisplay(string.Empty, string.Empty, null, primaryUnit, state.SecondaryUnit);
        }

        var conversion = CalculatorConversion.Convert(converter, rates, value, primaryUnit, state.SecondaryUnit);
        if (!conversion.Ok || conversion.Value is null)
        {
            return new CalculatorConvertDisplay(primary, string.Empty, conversion.Error ?? "无法换算 / cannot convert", primaryUnit, state.SecondaryUnit);
        }

        return new CalculatorConvertDisplay(
            primary,
            $"{conversion.Value} {state.SecondaryUnit}".Trim(),
            null,
            primaryUnit,
            state.SecondaryUnit);
    }
}
