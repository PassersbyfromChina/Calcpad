using System.Globalization;
using System.Text.Json.Nodes;
using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;
using Calcpad.Core.Plugins;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugins.CalculatorKit;

/// <summary>
/// The four modes of the one calculator surface, exactly like iPadOS 18: Basic, Scientific, Convert
/// and Math Notes. Switching modes never clears what the user is typing, which is why Basic and
/// Scientific share a single expression.
/// </summary>
public enum CalculatorMode
{
    /// <summary>Portrait keypad with the four operations.</summary>
    Basic,

    /// <summary>Landscape keypad with functions, constants, 2nd and the memory keys.</summary>
    Scientific,

    /// <summary>Basic keypad plus two unit lines; the expression is evaluated in the active unit.</summary>
    Convert,

    /// <summary>A note page rendered by the Math Notes plugin through the shared widget contract.</summary>
    MathNotes
}

/// <summary>Ids, labels and parsing for <see cref="CalculatorMode"/>.</summary>
public static class CalculatorModes
{
    /// <summary>Every mode in switcher order.</summary>
    public static IReadOnlyList<CalculatorMode> All { get; } =
    [
        CalculatorMode.Basic,
        CalculatorMode.Scientific,
        CalculatorMode.Convert,
        CalculatorMode.MathNotes
    ];

    /// <summary>Stable id written into the widget state.</summary>
    public static string Id(CalculatorMode mode) => mode switch
    {
        CalculatorMode.Scientific => "scientific",
        CalculatorMode.Convert => "convert",
        CalculatorMode.MathNotes => "math.notes",
        _ => "basic"
    };

    /// <summary>English label.</summary>
    public static string Label(CalculatorMode mode) => mode switch
    {
        CalculatorMode.Scientific => "Scientific",
        CalculatorMode.Convert => "Convert",
        CalculatorMode.MathNotes => "Math Notes",
        _ => "Basic"
    };

    /// <summary>Chinese label, shown next to the English one.</summary>
    public static string LabelZh(CalculatorMode mode) => mode switch
    {
        CalculatorMode.Scientific => "科学",
        CalculatorMode.Convert => "换算",
        CalculatorMode.MathNotes => "数学笔记",
        _ => "基础"
    };

    /// <summary>Short label used on the mode chip.</summary>
    public static string Chip(CalculatorMode mode) => $"{LabelZh(mode)} {Label(mode)}";

    /// <summary>Parses a state id; unknown ids fall back to Basic.</summary>
    public static bool TryParse(string? id, out CalculatorMode mode)
    {
        switch ((id ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "scientific":
                mode = CalculatorMode.Scientific;
                return true;
            case "convert":
                mode = CalculatorMode.Convert;
                return true;
            case "math.notes" or "mathnotes" or "notes":
                mode = CalculatorMode.MathNotes;
                return true;
            case "basic":
                mode = CalculatorMode.Basic;
                return true;
            default:
                mode = CalculatorMode.Basic;
                return false;
        }
    }
}

/// <summary>One page of a paged keypad.</summary>
public sealed record CalculatorKeypadPage(string Id, string Label, int Rows, int Columns, IReadOnlyList<CalculatorKey> Keys);

/// <summary>
/// Narrow-width keypads. The wide scientific pad is ten columns; below
/// <see cref="NarrowThreshold"/> the same keys are paged into two six column pages so nothing
/// overlaps and every key stays reachable in portrait.
/// </summary>
public static class CalculatorKeypadPages
{
    /// <summary>Widget width below which the scientific keypad switches to pages.</summary>
    public const double NarrowThreshold = 620;

    /// <summary>Rows of a narrow page.</summary>
    public const int NarrowRows = 5;

    /// <summary>Columns of a narrow page.</summary>
    public const int NarrowColumns = 6;

    private static readonly HashSet<string> MainPageIds =
    [
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        "dot", "eq", "div", "mul", "sub", "add",
        "ac", "back", "sign", "percent", "lparen", "rparen", "ee"
    ];

    /// <summary>Two pages that together cover every key of the wide scientific pad.</summary>
    public static IReadOnlyList<CalculatorKeypadPage> ScientificNarrow { get; } = BuildScientificNarrow();

    /// <summary>True when this widget width needs the paged keypad.</summary>
    public static bool NeedsPaging(double width) => width < NarrowThreshold;

    private static IReadOnlyList<CalculatorKeypadPage> BuildScientificNarrow()
    {
        var wide = CalculatorKeypads.Scientific;
        var main = Fill("main", "1/2", wide.Where(k => MainPageIds.Contains(k.Id)));
        var more = Fill("more", "2/2", wide.Where(k => !MainPageIds.Contains(k.Id)));
        return [main, more];
    }

    private static CalculatorKeypadPage Fill(string id, string label, IEnumerable<CalculatorKey> source)
    {
        var keys = new List<CalculatorKey>();
        var slot = 0;
        foreach (var key in source)
        {
            keys.Add(Place(key, slot++));
        }

        // The page toggle lives in the first free slot.
        if (slot < NarrowRows * NarrowColumns)
        {
            keys.Add(Place(new CalculatorKey("page", label, label, CalculatorKeyKind.Page, 0, 0), slot));
        }

        return new CalculatorKeypadPage(id, label, NarrowRows, NarrowColumns, keys);
    }

    private static CalculatorKey Place(CalculatorKey key, int slot) =>
        key with
        {
            Row = slot / NarrowColumns,
            Column = slot % NarrowColumns,
            RowSpan = 1,
            ColumnSpan = 1
        };
}

/// <summary>Command ids and type ids shared across the calculator family.</summary>
public static class CalculatorCommands
{
    /// <summary>Widget type of the single calculator surface.</summary>
    public const string SurfaceWidgetType = "calcpad.calculator.surface";

    /// <summary>Command that inserts the surface, optionally with a <c>mode</c> argument.</summary>
    public const string InsertCommandId = "calcpad.calculator.insert";

    /// <summary>Cross plugin command that refills a calculator from the history list.</summary>
    public const string RefillCommandId = "calcpad.calculator.refill";

    /// <summary>Command that focuses the history panel; the hamburger in the calculator calls it.</summary>
    public const string HistoryFocusCommandId = "calcpad.history.focus";

    /// <summary>Command that opens the calculator panel, where manual rates are edited.</summary>
    public const string RatesEditorCommandId = "calcpad.basic.calculator.panel";

    /// <summary>Cross plugin command that appends one calculation to the history.</summary>
    public const string HistoryAppendCommandId = "calcpad.history.append";

    /// <summary>Widget type contributed by the Math Notes plugin.</summary>
    public const string MathNotesWidgetType = "calcpad.math.notes";

    /// <summary>Plugin id of the Math Notes plugin, used to resolve its widget host.</summary>
    public const string MathNotesPluginId = "calcpad.math.notes";

    /// <summary>Command that inserts a Math Notes widget (used when no surface is open).</summary>
    public const string MathNotesInsertCommandId = "calcpad.math.notes.insert";

    /// <summary>Argument carrying the expression of a command.</summary>
    public const string ExpressionArgument = "expression";

    /// <summary>Argument carrying a result of a command.</summary>
    public const string ResultArgument = "result";

    /// <summary>Argument carrying a mode id.</summary>
    public const string ModeArgument = "mode";

    /// <summary>Argument carrying the text to copy.</summary>
    public const string TextArgument = "text";

    /// <summary>Shell command the calculator tries when no typed clipboard API is available.</summary>
    public const string ClipboardCopyCommandId = "calcpad.shell.clipboard.copy";
}

/// <summary>Convert-mode selection: category, the two units and the open picker.</summary>
public sealed class CalculatorConvertState
{
    /// <summary>Selected category id.</summary>
    public string CategoryId { get; set; } = "length";

    /// <summary>Unit the expression is evaluated in.</summary>
    public string PrimaryUnit { get; set; } = "km";

    /// <summary>Unit the second line converts to.</summary>
    public string SecondaryUnit { get; set; } = "mi";

    /// <summary>Open picker: empty, <c>primary</c>, <c>secondary</c>, <c>categories</c> or <c>rates</c>.</summary>
    public string Picker { get; set; } = string.Empty;

    /// <summary>Scroll offset of the unit picker.</summary>
    public int PickerScroll { get; set; }

    /// <summary>Swaps the two units, which is the swap button of the converter.</summary>
    public void Swap() => (PrimaryUnit, SecondaryUnit) = (SecondaryUnit, PrimaryUnit);

    /// <summary>Picks sensible units when the category changed or a stored unit is unknown.</summary>
    public void EnsureUnits(IReadOnlyList<UnitCategory> categories)
    {
        var category = categories.FirstOrDefault(c => string.Equals(c.Id, CategoryId, StringComparison.OrdinalIgnoreCase))
            ?? categories.FirstOrDefault();
        if (category is null || category.Units.Count == 0)
        {
            return;
        }

        CategoryId = category.Id;
        if (category.Find(PrimaryUnit) is null || category.Find(SecondaryUnit) is null ||
            string.Equals(PrimaryUnit, SecondaryUnit, StringComparison.OrdinalIgnoreCase))
        {
            var baseUnit = category.Units.FirstOrDefault(u => u.IsBase) ?? category.Units[0];
            var partner = category.Units.FirstOrDefault(u => !string.Equals(u.Symbol, baseUnit.Symbol, StringComparison.OrdinalIgnoreCase)
                                                             && u.Factor < baseUnit.Factor)
                          ?? category.Units.FirstOrDefault(u => !string.Equals(u.Symbol, baseUnit.Symbol, StringComparison.OrdinalIgnoreCase))
                          ?? baseUnit;
            PrimaryUnit = baseUnit.Symbol;
            SecondaryUnit = partner.Symbol;
        }
    }

    /// <summary>Writes the selection into a JSON object.</summary>
    public void Save(JsonObject target)
    {
        target["category"] = CategoryId;
        target["primary"] = PrimaryUnit;
        target["secondary"] = SecondaryUnit;
        target["picker"] = Picker.Length == 0 ? null : Picker;
        target["pickerScroll"] = PickerScroll;
    }

    /// <summary>Reads the selection back, tolerating garbage.</summary>
    public void Load(JsonObject? source)
    {
        if (source is null)
        {
            return;
        }

        CategoryId = Read(source, "category", CategoryId);
        PrimaryUnit = Read(source, "primary", PrimaryUnit);
        SecondaryUnit = Read(source, "secondary", SecondaryUnit);
        Picker = Read(source, "picker", string.Empty);
        if (source["pickerScroll"] is { } scroll && int.TryParse(scroll.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            PickerScroll = Math.Max(0, value);
        }
    }

    private static string Read(JsonObject source, string key, string fallback) =>
        source.TryGetPropertyValue(key, out var node) && node is not null ? node.ToString() : fallback;
}

/// <summary>
/// The single state object of the calculator surface: the mode, the shared expression, the memory,
/// the convert selection and the opaque Math Notes page.
/// </summary>
public sealed class CalculatorSurfaceState
{
    /// <summary>State key holding the mode id.</summary>
    public const string ModeKey = "mode";

    /// <summary>State key holding the convert selection.</summary>
    public const string ConvertKey = "convert";

    /// <summary>State key holding the Math Notes page; owned by the Math Notes plugin.</summary>
    public const string NoteKey = "note";

    /// <summary>Creates the state around an expression engine.</summary>
    public CalculatorSurfaceState(IExpressionEngine engine) => Calculator = new CalculatorState(engine);

    /// <summary>Active mode.</summary>
    public CalculatorMode Mode { get; set; } = CalculatorMode.Basic;

    /// <summary>Expression state shared by Basic and Scientific (and by the Convert display).</summary>
    public CalculatorState Calculator { get; }

    /// <summary>Convert-mode selection.</summary>
    public CalculatorConvertState Convert { get; } = new();

    /// <summary>Opaque note page; this plugin never reads or edits its contents.</summary>
    public JsonObject? Note { get; set; }

    /// <summary>True once the Math Notes renderer was attached to the page for this widget.</summary>
    public bool NoteAttached { get; set; }

    /// <summary>True while the mode menu popup is open.</summary>
    public bool ShowModeMenu { get; set; }

    /// <summary>Visible page of the paged scientific keypad.</summary>
    public int ScientificPage { get; set; }

    /// <summary>
    /// True when pressing <c>=</c> should be recorded in the history. Apple keeps Math Notes out of the
    /// history, and so do we.
    /// </summary>
    public bool RecordsHistory => Mode is CalculatorMode.Basic or CalculatorMode.Scientific or CalculatorMode.Convert;

    /// <summary>Switches mode without touching the expression, the memory or the note.</summary>
    public void SetMode(CalculatorMode mode)
    {
        Mode = mode;
        ShowModeMenu = false;
        Convert.Picker = string.Empty;
        Calculator.Hint = string.Empty;
    }

    /// <summary>Writes the whole surface state, keeping any foreign keys already present.</summary>
    public void Save(JsonObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Calculator.Save(target);
        target[ModeKey] = CalculatorModes.Id(Mode);
        target["sciPage"] = ScientificPage;
        target["showModeMenu"] = ShowModeMenu;
        target["noteAttached"] = NoteAttached;
        var convert = new JsonObject();
        Convert.Save(convert);
        target[ConvertKey] = convert;

        // The note page belongs to the Math Notes plugin: pass the very same node through when it is
        // already in place. Re-assigning a node that already sits under this key would throw
        // ("the node already has a parent"), which used to abort the save and let the note plugin
        // re-attach - wiping the user's note. When the node lives elsewhere it is copied verbatim.
        if (Note is not null && !ReferenceEquals(target[NoteKey], Note))
        {
            target[NoteKey] = Note.Parent is null ? Note : Note.DeepClone();
        }
    }

    /// <summary>Reads the surface state, tolerating missing or malformed fields.</summary>
    public void Load(JsonObject? source)
    {
        if (source is null)
        {
            return;
        }

        Calculator.Load(source);
        if (CalculatorModes.TryParse(source[ModeKey]?.ToString(), out var mode))
        {
            Mode = mode;
        }

        Convert.Load(source[ConvertKey] as JsonObject);
        Note = source[NoteKey] as JsonObject;
        ShowModeMenu = Flag(source, "showModeMenu");
        NoteAttached = Flag(source, "noteAttached");
        if (source["sciPage"] is { } page && int.TryParse(page.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            ScientificPage = Math.Max(0, index);
        }
    }

    private static bool Flag(JsonObject source, string key) =>
        source.TryGetPropertyValue(key, out var node) && node is not null &&
        bool.TryParse(node.ToString(), out var value) && value;

    /// <summary>Reads the state out of a widget object.</summary>
    public static CalculatorSurfaceState From(WidgetObject target, IExpressionEngine engine)
    {
        var state = new CalculatorSurfaceState(engine);
        target.State ??= new JsonObject();
        state.Load(target.State);
        return state;
    }
}

/// <summary>Creates calculator surface widgets for the whole calculator family.</summary>
public static class CalculatorSurfaceFactory
{
    /// <summary>Default size of a surface in basic or convert mode.</summary>
    public static CSize DefaultSize(CalculatorMode mode) => mode switch
    {
        CalculatorMode.Scientific => new CSize(760, 460),
        CalculatorMode.MathNotes => new CSize(560, 620),
        CalculatorMode.Convert => new CSize(380, 600),
        _ => new CSize(360, 560)
    };

    /// <summary>Builds a widget object with the state a surface expects.</summary>
    public static WidgetObject CreateWidget(
        CalculatorMode mode,
        IExpressionEngine? engine = null,
        CRect? bounds = null,
        string ownerPluginId = "calcpad.basic.calculator")
    {
        var size = DefaultSize(mode);
        var widget = new WidgetObject
        {
            WidgetType = CalculatorCommands.SurfaceWidgetType,
            Title = "Calculator / 计算器",
            OwnerPluginId = ownerPluginId,
            Bounds = bounds ?? new CRect(120, 120, size.Width, size.Height)
        };

        var state = new CalculatorSurfaceState(engine ?? MathServices.CreateExpressionEngine());
        state.SetMode(mode);
        widget.State = new JsonObject();
        state.Save(widget.State);
        return widget;
    }

    /// <summary>Reads the mode a widget state asks for, defaulting to Basic.</summary>
    public static CalculatorMode ModeOf(JsonObject? state) =>
        state is not null && CalculatorModes.TryParse(state[CalculatorSurfaceState.ModeKey]?.ToString(), out var mode)
            ? mode
            : CalculatorMode.Basic;
}
