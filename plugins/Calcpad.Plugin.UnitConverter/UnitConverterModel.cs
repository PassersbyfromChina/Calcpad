using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.UnitConverter;

/// <summary>
/// Pure geometry of the converter card, computed from the logical size so the widget can be resized
/// freely. Everything the pointer handler needs is a rectangle or a circle in widget local space.
/// </summary>
public sealed class UnitConverterLayout
{
    /// <summary>Title strip.</summary>
    public CRect Title { get; init; }

    /// <summary>Category chips, in catalogue order.</summary>
    public IReadOnlyList<(UnitCategory Category, CRect Rect)> Chips { get; init; } = [];

    /// <summary>Card holding the source value.</summary>
    public CRect FromCard { get; init; }

    /// <summary>Card holding the target value.</summary>
    public CRect ToCard { get; init; }

    /// <summary>Unit button of the source card.</summary>
    public CRect FromUnitButton { get; init; }

    /// <summary>Unit button of the target card.</summary>
    public CRect ToUnitButton { get; init; }

    /// <summary>Circular swap button between the two cards.</summary>
    public CRect SwapButton { get; init; }

    /// <summary>Area reserved for the unit picker; empty when no picker is open.</summary>
    public CRect PickerArea { get; init; }

    /// <summary>Unit pills inside the picker.</summary>
    public IReadOnlyList<(UnitInfo Unit, CRect Rect)> PickerItems { get; init; } = [];

    /// <summary>Button that reads a quantity from the canvas.</summary>
    public CRect FromCanvasButton { get; init; }

    /// <summary>Button that inserts the conversion onto the canvas.</summary>
    public CRect InsertButton { get; init; }

    /// <summary>Button that clears the input.</summary>
    public CRect ClearButton { get; init; }

    /// <summary>Computes the layout for a widget of the given logical size.</summary>
    public static UnitConverterLayout Compute(
        CSize size,
        IReadOnlyList<UnitCategory> categories,
        string? categoryId,
        string? picker,
        int chipsPerRow = 4,
        int pickerPerRow = 3)
    {
        const double padding = 12;
        var width = Math.Max(240, size.Width);
        var height = Math.Max(240, size.Height);

        var title = new CRect(padding, 10, width - (padding * 2), 26);

        var chipRows = (int)Math.Ceiling(categories.Count / (double)chipsPerRow);
        var chipHeight = 24d;
        var chipGap = 5d;
        var chips = new List<(UnitCategory Category, CRect Rect)>(categories.Count);
        for (var i = 0; i < categories.Count; i++)
        {
            var row = i / chipsPerRow;
            var column = i % chipsPerRow;
            var cellWidth = (title.Width - (chipGap * (chipsPerRow - 1))) / chipsPerRow;
            chips.Add((categories[i], new CRect(
                title.X + (column * (cellWidth + chipGap)),
                title.Bottom + 4 + (row * (chipHeight + chipGap)),
                cellWidth,
                chipHeight)));
        }

        var chipsBottom = chips.Count == 0 ? title.Bottom : chips[^1].Rect.Bottom;
        var footerHeight = 42d;
        var footerTop = height - padding - footerHeight;
        var cardsTop = chipsBottom + 12;
        var cardsHeight = Math.Clamp((footerTop - cardsTop) * 0.42, 96, 190);
        var cardWidth = (title.Width - 44) / 2;
        var fromCard = new CRect(title.X, cardsTop, cardWidth, cardsHeight);
        var toCard = new CRect(title.X + cardWidth + 44, cardsTop, cardWidth, cardsHeight);
        var fromUnitButton = new CRect(fromCard.X + 8, fromCard.Bottom - 34, fromCard.Width - 16, 26);
        var toUnitButton = new CRect(toCard.X + 8, toCard.Bottom - 34, toCard.Width - 16, 26);
        var swapButton = new CRect(fromCard.Right + 6, cardsTop + ((cardsHeight - 32) / 2), 32, 32);

        var pickerTop = fromCard.Bottom + 10;
        var pickerArea = picker is null
            ? CRect.Empty
            : new CRect(title.X, pickerTop, title.Width, Math.Max(0, footerTop - pickerTop - 6));
        var pickerItems = new List<(UnitInfo, CRect)>();
        if (picker is not null && categories.Count > 0)
        {
            var current = categories.FirstOrDefault(c => string.Equals(c.Id, categoryId, StringComparison.OrdinalIgnoreCase))
                ?? categories[0];
            var pillHeight = 28d;
            for (var i = 0; i < current.Units.Count; i++)
            {
                var row = i / pickerPerRow;
                var column = i % pickerPerRow;
                var cellWidth = (pickerArea.Width - (chipGap * (pickerPerRow - 1))) / pickerPerRow;
                var rect = new CRect(
                    pickerArea.X + (column * (cellWidth + chipGap)),
                    pickerArea.Y + (row * (pillHeight + chipGap)),
                    cellWidth,
                    pillHeight);
                if (rect.Bottom > pickerArea.Bottom && pickerArea.Height > 1)
                {
                    break;
                }

                pickerItems.Add((current.Units[i], rect));
            }
        }

        var buttonGap = 8d;
        var buttonWidth = (title.Width - (buttonGap * 2)) / 3;
        return new UnitConverterLayout
        {
            Title = title,
            Chips = chips,
            FromCard = fromCard,
            ToCard = toCard,
            FromUnitButton = fromUnitButton,
            ToUnitButton = toUnitButton,
            SwapButton = swapButton,
            PickerArea = pickerArea,
            PickerItems = pickerItems,
            FromCanvasButton = new CRect(title.X, footerTop, buttonWidth, footerHeight),
            InsertButton = new CRect(title.X + buttonWidth + buttonGap, footerTop, buttonWidth, footerHeight),
            ClearButton = new CRect(title.X + ((buttonWidth + buttonGap) * 2), footerTop, buttonWidth, footerHeight)
        };
    }
}

/// <summary>
/// The converter's state and behaviour, free of any Avalonia dependency: two units, two texts, live
/// two way conversion and a category selection. This is what the unit tests drive.
/// </summary>
public sealed class UnitConverterModel
{
    private readonly IUnitConverter _converter;

    /// <summary>Creates the model around a converter.</summary>
    public UnitConverterModel(IUnitConverter converter)
    {
        _converter = converter;
        Reset();
    }

    /// <summary>Selected category id.</summary>
    public string CategoryId { get; set; } = "length";

    /// <summary>Source unit symbol.</summary>
    public string FromUnit { get; set; } = string.Empty;

    /// <summary>Target unit symbol.</summary>
    public string ToUnit { get; set; } = string.Empty;

    /// <summary>Text typed on the source side.</summary>
    public string FromText { get; set; } = "1";

    /// <summary>Text shown on the target side.</summary>
    public string ToText { get; set; } = string.Empty;

    /// <summary>Which side the keyboard edits: <c>from</c> or <c>to</c>.</summary>
    public string Active { get; set; } = "from";

    /// <summary>Open unit picker side, or empty.</summary>
    public string Picker { get; set; } = string.Empty;

    /// <summary>Transient message drawn under the picker.</summary>
    public string Hint { get; set; } = string.Empty;

    /// <summary>The catalogue.</summary>
    public IReadOnlyList<UnitCategory> Categories => _converter.Categories;

    /// <summary>The selected category, never null.</summary>
    public UnitCategory CurrentCategory =>
        _converter.FindCategory(CategoryId) ?? _converter.Categories[0];

    /// <summary>Resets everything to the default state.</summary>
    public void Reset()
    {
        if (_converter.Categories.Count == 0)
        {
            return;
        }

        CategoryId = _converter.Categories[0].Id;
        FromText = "1";
        ToText = string.Empty;
        Active = "from";
        Picker = string.Empty;
        Hint = string.Empty;
        SelectDefaultUnits();
        ConvertNow("from");
    }

    /// <summary>Switches category and picks sensible default units.</summary>
    public void SelectCategory(string categoryId)
    {
        if (_converter.FindCategory(categoryId) is null)
        {
            return;
        }

        CategoryId = categoryId;
        Picker = string.Empty;
        Hint = string.Empty;
        SelectDefaultUnits();
        ConvertNow(Active);
    }

    /// <summary>Selects a unit for one side.</summary>
    public void SelectUnit(string side, string unit)
    {
        var info = CurrentCategory.Find(unit);
        if (info is null)
        {
            return;
        }

        if (string.Equals(side, "from", StringComparison.OrdinalIgnoreCase))
        {
            FromUnit = info.Symbol;
            Picker = string.Empty;
            ConvertNow("from");
            return;
        }

        ToUnit = info.Symbol;
        Picker = string.Empty;
        ConvertNow("to");
    }

    /// <summary>Swaps the two sides, keeping the displayed values consistent.</summary>
    public void Swap()
    {
        (FromUnit, ToUnit) = (ToUnit, FromUnit);
        var value = ToText;
        ToText = FromText;
        FromText = value;
        Active = string.Equals(Active, "from", StringComparison.Ordinal) ? "to" : "from";
        ConvertNow(Active);
    }

    /// <summary>Clears both sides.</summary>
    public void Clear()
    {
        FromText = string.Empty;
        ToText = string.Empty;
        Hint = string.Empty;
    }

    /// <summary>Appends typed text to the active side and reconverts.</summary>
    public void AppendText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (string.Equals(Active, "from", StringComparison.Ordinal))
        {
            FromText += text;
            ConvertNow("from");
            return;
        }

        ToText += text;
        ConvertNow("to");
    }

    /// <summary>Removes the last character of the active side and reconverts.</summary>
    public void Backspace()
    {
        if (string.Equals(Active, "from", StringComparison.Ordinal))
        {
            FromText = FromText.Length > 0 ? FromText[..^1] : string.Empty;
            ConvertNow("from");
            return;
        }

        ToText = ToText.Length > 0 ? ToText[..^1] : string.Empty;
        ConvertNow("to");
    }

    /// <summary>Applies a value parsed from the canvas to the active side.</summary>
    public void ApplyQuantity(double value, string unit)
    {
        var info = UnitCatalog.FindUnitWithCategory(unit);
        if (info is not null && string.Equals(info.Value.Category.Id, CategoryId, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(Active, "from", StringComparison.Ordinal))
            {
                FromUnit = info.Value.Unit.Symbol;
            }
            else
            {
                ToUnit = info.Value.Unit.Symbol;
            }
        }

        var text = NumberFormatter.Format(value, 12);
        if (string.Equals(Active, "from", StringComparison.Ordinal))
        {
            FromText = text;
            ConvertNow("from");
        }
        else
        {
            ToText = text;
            ConvertNow("to");
        }
    }

    /// <summary>Value of the active side, or null when it is not a number yet.</summary>
    public double? ActiveValue()
    {
        var text = string.Equals(Active, "from", StringComparison.Ordinal) ? FromText : ToText;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>Writes the model into a widget state object.</summary>
    public void Save(JsonObject target)
    {
        target["active"] = Active;
        target["category"] = CategoryId;
        target["fromUnit"] = FromUnit;
        target["toUnit"] = ToUnit;
        target["fromText"] = FromText;
        target["toText"] = ToText;
        target["picker"] = Picker.Length == 0 ? null : Picker;
        target["hint"] = Hint.Length == 0 ? null : Hint;
        target["updatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>Reads the model back from a widget state object, tolerating garbage.</summary>
    public void Load(JsonObject? source)
    {
        if (source is null)
        {
            return;
        }

        Active = Read(source, "active", "from");
        CategoryId = Read(source, "category", CategoryId);
        FromUnit = Read(source, "fromUnit", FromUnit);
        ToUnit = Read(source, "toUnit", ToUnit);
        FromText = Read(source, "fromText", FromText);
        ToText = Read(source, "toText", ToText);
        Picker = Read(source, "picker", string.Empty);
        Hint = Read(source, "hint", string.Empty);

        if (_converter.FindCategory(CategoryId) is null)
        {
            CategoryId = _converter.Categories[0].Id;
        }

        if (CurrentCategory.Find(FromUnit) is null || CurrentCategory.Find(ToUnit) is null)
        {
            SelectDefaultUnits();
        }
    }

    /// <summary>Reads a model out of a widget object, falling back to defaults.</summary>
    public static UnitConverterModel From(WidgetObject target, IUnitConverter converter)
    {
        var model = new UnitConverterModel(converter);
        model.Load(target.State);
        return model;
    }

    /// <summary>Converts one side into the other, leaving the target text untouched when it cannot.</summary>
    public void ConvertNow(string sourceSide)
    {
        var (valueText, from, to, otherSide) = string.Equals(sourceSide, "from", StringComparison.Ordinal)
            ? (FromText, FromUnit, ToUnit, "to")
            : (ToText, ToUnit, FromUnit, "from");

        if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return;
        }

        var result = _converter.Convert(value, from, to);
        if (!result.Ok || result.Value is null)
        {
            Hint = result.Error ?? "无法换算 / cannot convert";
            return;
        }

        Hint = string.Empty;
        if (string.Equals(otherSide, "to", StringComparison.Ordinal))
        {
            ToText = result.Value;
        }
        else
        {
            FromText = result.Value;
        }
    }

    private void SelectDefaultUnits()
    {
        var category = CurrentCategory;
        if (category.Units.Count == 0)
        {
            FromUnit = string.Empty;
            ToUnit = string.Empty;
            return;
        }

        var baseUnit = category.Units.FirstOrDefault(u => u.IsBase) ?? category.Units[0];
        var partner = category.Units.FirstOrDefault(u => !ReferenceEquals(u, baseUnit) && u.Factor < baseUnit.Factor)
            ?? category.Units.FirstOrDefault(u => !ReferenceEquals(u, baseUnit))
            ?? baseUnit;
        FromUnit = baseUnit.Symbol;
        ToUnit = partner.Symbol;
    }

    private static string Read(JsonObject source, string key, string fallback) =>
        source.TryGetPropertyValue(key, out var node) && node is not null ? node.ToString() : fallback;
}
