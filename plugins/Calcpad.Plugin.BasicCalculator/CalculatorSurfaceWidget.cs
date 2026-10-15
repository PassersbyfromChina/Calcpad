using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using Calcpad.Core.Canvas;
using Calcpad.Core.Numerics;
using Calcpad.Core.Plugins;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugins.CalculatorKit;

/// <summary>
/// The one calculator of Calcpad, modelled on iPadOS 18: a single widget with a mode switcher in its
/// bottom left corner offering Basic, Scientific, Convert and Math Notes. The four modes share one
/// state object, so parentheses typed on the scientific keypad are still there after switching back
/// to basic, and the memory register survives every switch.
/// </summary>
/// <remarks>
/// Convert mode evaluates the expression in the active unit and shows the conversion underneath.
/// Math Notes mode delegates rendering and input to the registered Math Notes widget renderer through
/// the ordinary widget contract; this widget only carries the page's opaque state.
/// </remarks>
public sealed class CalculatorSurfaceWidget : IWidgetRenderer
{
    private const double LongPressSeconds = 0.5;

    /// <summary>Horizontal travel on the display that deletes one character, like the iPad.</summary>
    private const double SwipeDeleteThreshold = 24;

    private readonly IExpressionEngine _engine;
    private readonly IUnitConverter _converter;
    private readonly CurrencyRateTable _rates;
    private readonly Dictionary<string, string> _pressed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _pressedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CPoint> _pressedPoint = new(StringComparer.Ordinal);
    private bool _settingsLoaded;

    /// <summary>Creates the surface.</summary>
    /// <param name="engine">Expression engine; a shared one is created when omitted.</param>
    /// <param name="converter">Unit converter; a shared one is created when omitted.</param>
    /// <param name="rates">Currency rate table; a seeded one is created when omitted.</param>
    public CalculatorSurfaceWidget(
        IExpressionEngine? engine = null,
        IUnitConverter? converter = null,
        CurrencyRateTable? rates = null)
    {
        _engine = engine ?? MathServices.CreateExpressionEngine();
        _converter = converter ?? MathServices.CreateUnitConverter();
        _rates = rates ?? new CurrencyRateTable();
    }

    /// <summary>UI registry used to find the Math Notes renderer; set by the plugin on initialize.</summary>
    public IUiRegistry? Ui { get; set; }

    /// <summary>True when the last math notes frame was painted by the delegated note renderer.</summary>
    public bool LastNoteDelegated { get; private set; }

    /// <summary>Why the last math notes frame fell back to the placeholder, or null.</summary>
    public string? LastNoteFailure { get; private set; }

    /// <summary>
    /// Performs the daily rate fetch on behalf of the surface. The plugin owns it because only the
    /// plugin holds the permission broker, so the widget itself never opens a connection.
    /// </summary>
    public Func<CurrencyRateTable, CancellationToken, ValueTask<bool>>? RatesRefresher { get; set; }

    /// <inheritdoc />
    public string WidgetType => CalculatorCommands.SurfaceWidgetType;

    /// <summary>The currency table this surface converts with.</summary>
    public CurrencyRateTable Rates => _rates;

    /// <summary>The converter used for non currency units.</summary>
    public IUnitConverter Converter => _converter;

    /// <inheritdoc />
    public CSize Measure(WidgetMeasureContext context) =>
        CalculatorSurfaceFactory.DefaultSize(CalculatorSurfaceFactory.ModeOf(context?.Object?.State));

    /// <inheritdoc />
    public void OnAttached(WidgetObject target, IWidgetHost host)
    {
        InitializeState(target);
        if (!_settingsLoaded && host is not null)
        {
            _rates.LoadFromSettings(host.Settings);
            _settingsLoaded = true;
        }
    }

    /// <summary>Initialises the widget state without needing a live host.</summary>
    public void InitializeState(WidgetObject target)
    {
        target.State ??= new JsonObject();
        var state = CalculatorSurfaceState.From(target, _engine);
        state.Convert.EnsureUnits(Categories);
        state.Save(target.State);
        target.State["pressed"] = null;
        target.State["hint"] = null;
    }

    /// <summary>All categories the converter offers, currency included.</summary>
    public IReadOnlyList<UnitCategory> Categories => CalculatorConversion.CategoriesWithCurrency(_converter, _rates);

    /// <summary>
    /// Geometry of the surface for a widget at a given size. Exposed for hosts that want to place
    /// tooltips, and for verification.
    /// </summary>
    public CalculatorSurfaceLayout LayoutFor(WidgetObject target, CSize size, PluginTheme? theme = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        var state = CalculatorSurfaceState.From(target, _engine);
        state.Convert.EnsureUnits(Categories);
        return BuildLayout(size, state, theme ?? PluginTheme.IpadDark);
    }

    /// <summary>
    /// Refills a surface from the history list. Used by the cross plugin refill command.
    /// </summary>
    public bool Refill(WidgetObject target, string expression, string? result)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        var state = CalculatorSurfaceState.From(target, _engine);
        state.Calculator.Tape = string.Empty;
        state.Calculator.Entry = expression.Trim();
        state.Calculator.LastResult = result ?? string.Empty;
        state.Calculator.JustEvaluated = false;
        state.Calculator.IsError = false;
        state.Calculator.Hint = "来自历史 / from history";
        state.SetMode(state.Mode);
        target.State ??= new JsonObject();
        state.Save(target.State);
        return true;
    }

    /// <inheritdoc />
    public void Render(WidgetRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var size = context.LogicalSize;
        if (size.Width <= 2 || size.Height <= 2)
        {
            return;
        }

        var theme = context.Theme;
        var drawing = context.Drawing;
        var state = CalculatorSurfaceState.From(context.Object, _engine);
        state.Convert.EnsureUnits(Categories);
        var layout = BuildLayout(size, state, theme);
        var pressedKey = ReadPressed(context.Object);

        CalculatorPaint.DrawPanel(drawing, layout.Panel, theme.SurfaceBrush, 24);

        // --- top chrome: history entry on the left, status on the right ------------------------
        DrawHistoryIcon(drawing, theme, layout.HistoryButton);
        var status = BuildStatusText(state, layout);
        CalculatorPaint.DrawTextIn(drawing, status, layout.TopStatus, 11, theme.Brush(theme.TextSecondary), CalculatorPaint.TextAlign.Left);

        // --- display ---------------------------------------------------------------------------
        CalculatorPaint.DrawTextIn(
            drawing,
            state.Calculator.ExpressionText,
            layout.ExpressionArea,
            Math.Clamp(layout.DisplayArea.Height * 0.16, 10, 17),
            theme.Brush(theme.TextSecondary),
            CalculatorPaint.TextAlign.Right);

        if (state.Mode == CalculatorMode.Convert)
        {
            DrawConvertDisplay(drawing, theme, layout, state);
        }
        else
        {
            CalculatorPaint.DrawTextIn(
                drawing,
                state.Calculator.DisplayText,
                layout.ValueArea,
                Math.Clamp(layout.DisplayArea.Height * 0.34, 18, 40),
                theme.Brush(state.Calculator.IsError ? theme.Danger : theme.TextPrimary),
                CalculatorPaint.TextAlign.Right,
                bold: true,
                maxSize: Math.Clamp(layout.DisplayArea.Height * 0.62, 24, 64));
        }

        if (layout.MemoryRow.Height > 0)
        {
            DrawMemoryRow(drawing, theme, layout, state, pressedKey);
        }

        if (state.Mode == CalculatorMode.Convert)
        {
            DrawConvertChrome(drawing, theme, layout, state);
        }

        // --- content ---------------------------------------------------------------------------
        if (state.Mode == CalculatorMode.MathNotes)
        {
            RenderNotePage(context, state, layout);
        }
        else if (layout.PickerArea.Height > 1)
        {
            DrawPicker(drawing, theme, layout, state);
        }
        else
        {
            foreach (var (key, rect) in layout.Keys)
            {
                var isPressed = string.Equals(pressedKey, key.Id, StringComparison.Ordinal);
                var active = (key.Kind == CalculatorKeyKind.Second && state.Calculator.SecondActive)
                             || (key.Kind == CalculatorKeyKind.Angle && state.Calculator.AngleUnit != AngleUnit.Degrees)
                             || (key.Kind == CalculatorKeyKind.Page && state.ScientificPage > 0);
                var (fill, text) = CalculatorColors.For(key, theme, isPressed, active);
                CalculatorPaint.DrawKey(drawing, rect, theme.Brush(fill), layout.RoundKeys && KeypadGeometry.IsRound(key, rect, true));
                CalculatorPaint.DrawTextIn(drawing, LabelFor(key, state), rect, Math.Clamp(rect.Height * 0.36, 10, 26), theme.Brush(text), CalculatorPaint.TextAlign.Center, bold: key.Kind is CalculatorKeyKind.Equals or CalculatorKeyKind.Operator, maxSize: Math.Clamp(rect.Height * 0.5, 12, 32));
            }
        }

        // --- bottom chrome: mode switcher in the bottom left corner ----------------------------
        var modeFill = state.ShowModeMenu ? theme.Accent : theme.SurfaceAlt;
        CalculatorPaint.DrawPanel(drawing, layout.ModeButton, theme.Brush(modeFill), 10);
        CalculatorPaint.DrawTextIn(
            drawing,
            $"⌗ {CalculatorModes.Chip(state.Mode)}",
            layout.ModeButton,
            11,
            theme.Brush(state.ShowModeMenu ? theme.AccentText : theme.TextPrimary),
            CalculatorPaint.TextAlign.Center,
            bold: true,
            maxSize: 12);

        if (layout.PageButton.Height > 0)
        {
            CalculatorPaint.DrawPanel(drawing, layout.PageButton, theme.Brush(theme.SurfaceAlt), 10);
            CalculatorPaint.DrawTextIn(
                drawing,
                $"{state.ScientificPage + 1}/{CalculatorKeypadPages.ScientificNarrow.Count}",
                layout.PageButton,
                11,
                theme.Brush(theme.TextPrimary),
                CalculatorPaint.TextAlign.Center,
                bold: true);
        }

        CalculatorPaint.DrawTextIn(
            drawing,
            state.Calculator.Hint,
            layout.HintArea,
            Math.Clamp(layout.HintArea.Height * 0.5, 9, 12),
            theme.Brush(state.Calculator.IsError ? theme.Danger : theme.TextSecondary),
            CalculatorPaint.TextAlign.Right);

        // --- popups ----------------------------------------------------------------------------
        if (state.ShowModeMenu)
        {
            DrawModeMenu(drawing, theme, layout, state);
        }

        if (context.IsSelected)
        {
            drawing.DrawRectangle(
                null,
                new Pen(theme.Brush(theme.Selection), 2),
                new Avalonia.RoundedRect(CalculatorPaint.ToRect(new CRect(1, 1, size.Width - 2, size.Height - 2)), 24));
        }
    }

    /// <inheritdoc />
    public bool OnPointer(WidgetPointerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var state = CalculatorSurfaceState.From(context.Object, _engine);
        state.Convert.EnsureUnits(Categories);
        var layout = BuildLayout(context.LogicalSize, state, context.Host.Theme);
        var key = context.Object.Id ?? string.Empty;

        switch (context.Phase)
        {
            case WidgetPointerPhase.Down:
            {
                var action = CalculatorSurfaceLayout.HitTest(layout, context.Position);

                // The note page owns the content area, but the chrome stays alive so the mode
                // switcher and the history icon always work - they are the way out of the mode.
                if (state.Mode == CalculatorMode.MathNotes && !IsChromeAction(action.Kind))
                {
                    return ForwardNotePointer(context, state, layout, WidgetPointerPhase.Down);
                }

                if (action.Kind == CalculatorSurfaceActionKind.None)
                {
                    return false;
                }

                _pressed[key] = Encode(action);
                _pressedAt[key] = Environment.TickCount64 / 1000.0;
                _pressedPoint[key] = context.Position;
                WritePressed(context.Object, Encode(action));
                context.Host.Invalidate();
                return true;
            }

            case WidgetPointerPhase.Up:
            {
                var encoded = ReadPressed(context.Object) ?? (_pressed.TryGetValue(key, out var pending) ? pending : null);
                var pressedAction = encoded is null ? new CalculatorSurfaceAction(CalculatorSurfaceActionKind.None) : Decode(encoded);
                if (state.Mode == CalculatorMode.MathNotes && !IsChromeAction(pressedAction.Kind))
                {
                    WritePressed(context.Object, null);
                    _pressed.Remove(key);
                    _pressedAt.Remove(key);
                    _pressedPoint.Remove(key);
                    return ForwardNotePointer(context, state, layout, WidgetPointerPhase.Up);
                }

                WritePressed(context.Object, null);
                _pressed.Remove(key);
                if (encoded is null)
                {
                    return false;
                }

                var action = pressedAction;
                var held = _pressedAt.TryGetValue(key, out var started) ? (Environment.TickCount64 / 1000.0) - started : 0;
                var swipe = _pressedPoint.TryGetValue(key, out var startedPoint) ? context.Position.X - startedPoint.X : 0;
                _pressedAt.Remove(key);
                _pressedPoint.Remove(key);

                // The iPad deletes one character when the display is swiped sideways, which is the
                // only on screen delete the portrait keypad has.
                if (action.Kind == CalculatorSurfaceActionKind.RecallResult && Math.Abs(swipe) >= SwipeDeleteThreshold)
                {
                    state.Calculator.Backspace();
                    Save(context.Object, state);
                    context.Host.Invalidate();
                    return true;
                }

                var current = CalculatorSurfaceLayout.HitTest(layout, context.Position);
                var sameAction = current.Kind == action.Kind
                                 && string.Equals(current.Id, action.Id, StringComparison.Ordinal)
                                 && current.Mode == action.Mode;
                if (sameAction || IsPopupAction(action.Kind))
                {
                    Perform(context, state, layout, action, held);
                }

                context.Host.Invalidate();
                return true;
            }

            case WidgetPointerPhase.Cancel:
                if (state.Mode == CalculatorMode.MathNotes)
                {
                    return ForwardNotePointer(context, state, layout, WidgetPointerPhase.Cancel);
                }

                WritePressed(context.Object, null);
                _pressed.Remove(key);
                _pressedAt.Remove(key);
                _pressedPoint.Remove(key);
                context.Host.Invalidate();
                return true;

            case WidgetPointerPhase.Wheel:
                if (state.Mode == CalculatorMode.MathNotes)
                {
                    return ForwardNotePointer(context, state, layout, WidgetPointerPhase.Wheel);
                }

                if (layout.PickerArea.Height > 1)
                {
                    var direction = context.WheelDelta > 0 ? -1 : 1;
                    state.Convert.PickerScroll = Math.Max(0, state.Convert.PickerScroll + direction);
                    Save(context.Object, state);
                    context.Host.Invalidate();
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    /// <inheritdoc />
    public bool OnKey(WidgetKeyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var state = CalculatorSurfaceState.From(context.Object, _engine);
        state.Convert.EnsureUnits(Categories);
        var theme = context.Host.Theme;
        var layout = BuildLayout(new CSize(context.Object.Bounds.Width, context.Object.Bounds.Height), state, theme);

        if (state.Mode == CalculatorMode.MathNotes)
        {
            return ForwardNoteKey(context, state, layout);
        }

        var normalized = CalculatorKeyboard.Normalize(context.Key);
        if (normalized.Length == 0)
        {
            return false;
        }

        // Modal keys first: the picker and the mode menu swallow Escape.
        if (string.Equals(normalized, "Escape", StringComparison.Ordinal) || string.Equals(normalized, "Clear", StringComparison.Ordinal))
        {
            if (state.ShowModeMenu || state.Convert.Picker.Length > 0)
            {
                state.ShowModeMenu = false;
                state.Convert.Picker = string.Empty;
                Save(context.Object, state);
                context.Host.Invalidate();
                return true;
            }
        }

        var key = layout.Keys.FirstOrDefault(k => string.Equals(k.Key.Id, normalized, StringComparison.Ordinal)).Key;
        if (key is not null)
        {
            Press(context.Object, context.Host, state, key, 0);
            return true;
        }

        switch (normalized)
        {
            case "+":
            case "-":
            case "*":
            case "/":
            case "=":
            {
                var target = layout.Keys.FirstOrDefault(k => k.Key.Kind == CalculatorKeyKind.Operator && k.Key.Primary == normalized).Key
                             ?? layout.Keys.FirstOrDefault(k => k.Key.Kind == CalculatorKeyKind.Equals).Key;
                if (target is not null)
                {
                    Press(context.Object, context.Host, state, target, 0);
                    return true;
                }

                break;
            }

            case "Backspace":
                Mutate(context.Object, context.Host, state, s => s.Backspace());
                return true;

            case "Clear":
                Mutate(context.Object, context.Host, state, s => s.ClearAll());
                return true;

            case "%":
                Mutate(context.Object, context.Host, state, s => s.ApplyPercent());
                return true;

            case "±":
                Mutate(context.Object, context.Host, state, s => s.ToggleSign());
                return true;

            case "^":
            case "(":
            case ")":
            case ".":
            case "!":
            case "π":
            case "e":
                Mutate(context.Object, context.Host, state, s => s.AppendText(normalized));
                return true;
        }

        if (normalized.Length == 1 && (char.IsDigit(normalized[0]) || char.IsLetter(normalized[0])))
        {
            Mutate(context.Object, context.Host, state, s => s.AppendText(normalized));
            return true;
        }

        return false;
    }

    // ----- actions ------------------------------------------------------------------------------

    private void Perform(
        WidgetPointerContext context,
        CalculatorSurfaceState state,
        CalculatorSurfaceLayout layout,
        CalculatorSurfaceAction action,
        double heldSeconds)
    {
        switch (action.Kind)
        {
            case CalculatorSurfaceActionKind.Key:
            {
                var key = layout.Keys.FirstOrDefault(k => string.Equals(k.Key.Id, action.Id, StringComparison.Ordinal)).Key;
                if (key is not null)
                {
                    Press(context.Object, context.Host, state, key, heldSeconds);
                }

                break;
            }

            case CalculatorSurfaceActionKind.SetMode:
                state.SetMode(action.Mode);
                Save(context.Object, state);
                break;

            case CalculatorSurfaceActionKind.ToggleModeMenu:
                state.ShowModeMenu = !state.ShowModeMenu;
                state.Convert.Picker = string.Empty;
                Save(context.Object, state);
                break;

            case CalculatorSurfaceActionKind.OpenHistory:
                Invoke(context.Host, CalculatorCommands.HistoryFocusCommandId);
                state.Calculator.Hint = "历史记录 / history";
                Save(context.Object, state);
                break;

            case CalculatorSurfaceActionKind.MemoryClear:
                state.Calculator.MemoryClear();
                Save(context.Object, state);
                break;

            case CalculatorSurfaceActionKind.MemoryAdd:
                state.Calculator.MemoryAdd();
                Save(context.Object, state);
                break;

            case CalculatorSurfaceActionKind.MemorySubtract:
                state.Calculator.MemorySubtract();
                Save(context.Object, state);
                break;

            case CalculatorSurfaceActionKind.MemoryRecall:
                state.Calculator.MemoryRecall();
                Save(context.Object, state);
                break;

            case CalculatorSurfaceActionKind.RecallResult:
                state.Calculator.RecallLastForEdit();
                Save(context.Object, state);
                break;

            case CalculatorSurfaceActionKind.SwapUnits:
                state.Convert.Swap();
                state.Calculator.Hint = "已对调 / swapped";
                Save(context.Object, state);
                break;

            case CalculatorSurfaceActionKind.OpenPrimaryPicker:
                state.Convert.Picker = "primary";
                state.Convert.PickerScroll = 0;
                Save(context.Object, state);
                break;

            case CalculatorSurfaceActionKind.OpenSecondaryPicker:
                state.Convert.Picker = "secondary";
                state.Convert.PickerScroll = 0;
                Save(context.Object, state);
                break;

            case CalculatorSurfaceActionKind.OpenCategoryPicker:
                state.Convert.Picker = "categories";
                state.Convert.PickerScroll = 0;
                Save(context.Object, state);
                break;

            case CalculatorSurfaceActionKind.ClosePicker:
                state.Convert.Picker = string.Empty;
                Save(context.Object, state);
                break;

            case CalculatorSurfaceActionKind.PickPrimaryUnit:
                if (action.Id is { Length: > 0 })
                {
                    if (string.Equals(state.Convert.Picker, "secondary", StringComparison.Ordinal))
                    {
                        state.Convert.SecondaryUnit = action.Id;
                    }
                    else
                    {
                        state.Convert.PrimaryUnit = action.Id;
                    }

                    state.Convert.Picker = string.Empty;
                    Save(context.Object, state);
                }

                break;

            case CalculatorSurfaceActionKind.PickCategory:
                if (action.Id is { Length: > 0 })
                {
                    state.Convert.CategoryId = action.Id;
                    state.Convert.Picker = string.Empty;
                    state.Convert.EnsureUnits(Categories);
                    Save(context.Object, state);
                }

                break;

            case CalculatorSurfaceActionKind.ToggleRates:
                if (_rates.Source == CurrencyRateSource.Manual)
                {
                    _rates.UseDailyRates();
                    state.Calculator.Hint = CurrencyRateTable.UnavailableLabel;
                    _ = RefreshRatesAsync(context.Object, context.Host, state);
                }
                else
                {
                    _rates.UseManualRates();
                    state.Calculator.Hint = CurrencyRateTable.ManualLabel;
                    PersistRates(context.Host);
                }

                Save(context.Object, state);
                break;

            case CalculatorSurfaceActionKind.RefreshRates:
                _ = RefreshRatesAsync(context.Object, context.Host, state);
                break;

            case CalculatorSurfaceActionKind.OpenRatesEditor:
                Invoke(context.Host, CalculatorCommands.RatesEditorCommandId);
                state.Calculator.Hint = "编辑手动汇率 / edit manual rates";
                Save(context.Object, state);
                break;

            case CalculatorSurfaceActionKind.NextPage:
                state.ScientificPage = (state.ScientificPage + 1) % Math.Max(1, CalculatorKeypadPages.ScientificNarrow.Count);
                Save(context.Object, state);
                break;
        }
    }

    /// <summary>Applies a key press, recording the calculation in the history when the mode allows it.</summary>
    private void Press(WidgetObject target, IWidgetHost host, CalculatorSurfaceState state, CalculatorKey key, double heldSeconds)
    {
        // Long pressing AC or the delete key clears everything, like holding Delete on the iPad.
        if (heldSeconds >= LongPressSeconds && key.Id is "ac" or "back")
        {
            state.Calculator.ClearAll();
            state.Calculator.Hint = "AC";
            Save(target, state);
            host.Invalidate();
            return;
        }

        var before = state.Calculator.LastExpression;
        state.Calculator.Press(key);
        Save(target, state);
        if (state.RecordsHistory
            && key.Kind == CalculatorKeyKind.Equals
            && !state.Calculator.IsError
            && state.Calculator.LastExpression.Length > 0
            && !string.Equals(before, state.Calculator.LastExpression, StringComparison.Ordinal))
        {
            CalculatorHistory.TryAppend(host, state.Calculator.LastExpression, ResultForHistory(state));
        }

        host.Invalidate();
    }

    private void Mutate(WidgetObject target, IWidgetHost host, CalculatorSurfaceState state, Action<CalculatorState> mutate)
    {
        mutate(state.Calculator);
        Save(target, state);
        host.Invalidate();
    }

    /// <summary>Result text that reaches the history: quantities keep their unit in convert mode.</summary>
    private string ResultForHistory(CalculatorSurfaceState state)
    {
        if (state.Mode != CalculatorMode.Convert || state.Calculator.LastResult.Length == 0)
        {
            return state.Calculator.LastResult;
        }

        var display = CalculatorConvertFormatter.Build(state.Calculator.LastResult, state.Convert, _converter, _rates);
        return display.PrimaryText.Length > 0 ? display.PrimaryText : state.Calculator.LastResult;
    }

    private async Task RefreshRatesAsync(WidgetObject target, IWidgetHost host, CalculatorSurfaceState state)
    {
        try
        {
            var refresher = RatesRefresher;
            var ok = refresher is not null && await refresher(_rates, CancellationToken.None).ConfigureAwait(true);
            state.Calculator.Hint = ok ? _rates.SourceLabel : CurrencyRateTable.UnavailableLabel;
            PersistRates(host);
            Save(target, state);
            await host.Host.DispatchAsync(host.Invalidate).ConfigureAwait(true);
        }
        catch (Exception)
        {
            state.Calculator.Hint = CurrencyRateTable.UnavailableLabel;
            Save(target, state);
        }
    }

    private void PersistRates(IWidgetHost host)
    {
        try
        {
            _rates.SaveToSettings(host.Settings);
        }
        catch
        {
            // Settings are best effort.
        }
    }

    private static void Invoke(IWidgetHost host, string commandId)
    {
        try
        {
            var pending = host.InvokeCommandAsync(commandId, null);
            if (!pending.IsCompletedSuccessfully)
            {
                Observe(pending);
            }
        }
        catch
        {
            // A missing plugin or a denied permission must never break the keypad.
        }
    }

    private static async void Observe(ValueTask pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch
        {
            // ignored on purpose
        }
    }

    // ----- Math Notes delegation -----------------------------------------------------------------

    private void RenderNotePage(WidgetRenderContext context, CalculatorSurfaceState state, CalculatorSurfaceLayout layout)
    {
        var renderer = Ui?.FindWidget(CalculatorCommands.MathNotesWidgetType);
        var foreignHost = ResolveForeignHost(context.Host, CalculatorCommands.MathNotesPluginId);
        if (renderer is null || foreignHost is null)
        {
            LastNoteDelegated = false;
            LastNoteFailure = renderer is null
                ? "the math notes plugin is not enabled"
                : "the shell did not provide a widget host for the math notes plugin";
            CalculatorPaint.DrawPanel(context.Drawing, layout.NoteArea, context.Theme.Brush(context.Theme.SurfaceAlt), 14);
            CalculatorPaint.DrawTextIn(
                context.Drawing,
                "数学笔记 / Math Notes\n插件未启用，或外壳未提供跨插件宿主。\n点按此处可插入独立笔记 / tap to insert a note widget.",
                layout.NoteArea,
                12,
                context.Theme.Brush(context.Theme.TextSecondary),
                CalculatorPaint.TextAlign.Center);
            return;
        }

        var page = BuildNotePage(context.Object, state, layout);
        if (!state.NoteAttached)
        {
            try
            {
                renderer.OnAttached(page, foreignHost);
                state.NoteAttached = true;
                Save(context.Object, state);
            }
            catch (Exception ex)
            {
                LastNoteFailure = $"attaching the note page failed: {ex.GetType().Name}";
            }
        }

        var screen = new CRect(
            context.Bounds.X + (layout.NoteArea.X * context.Zoom),
            context.Bounds.Y + (layout.NoteArea.Y * context.Zoom),
            layout.NoteArea.Width * context.Zoom,
            layout.NoteArea.Height * context.Zoom);

        try
        {
            renderer.Render(new WidgetRenderContext
            {
                Drawing = context.Drawing,
                Bounds = screen,
                Object = page,
                Host = foreignHost,
                Theme = context.Theme,
                Zoom = context.Zoom,
                IsSelected = false,
                IsFocused = context.IsFocused
            });
            LastNoteDelegated = true;
            LastNoteFailure = null;
        }
        catch (Exception ex)
        {
            LastNoteDelegated = false;
            LastNoteFailure = $"the note renderer threw {ex.GetType().Name}";
            CalculatorPaint.DrawTextIn(
                context.Drawing,
                $"数学笔记渲染失败 / Math Notes failed: {ex.GetType().Name}",
                layout.NoteArea,
                11,
                context.Theme.Brush(context.Theme.Danger),
                CalculatorPaint.TextAlign.Center);
        }
    }

    private bool ForwardNotePointer(WidgetPointerContext context, CalculatorSurfaceState state, CalculatorSurfaceLayout layout, WidgetPointerPhase phase)
    {
        var renderer = Ui?.FindWidget(CalculatorCommands.MathNotesWidgetType);
        var foreignHost = ResolveForeignHost(context.Host, CalculatorCommands.MathNotesWidgetType);
        if (renderer is null || foreignHost is null)
        {
            // Without a note surface a tap inserts a standalone note widget so the user is not stuck.
            if (phase == WidgetPointerPhase.Up)
            {
                Invoke(context.Host, CalculatorCommands.MathNotesInsertCommandId);
                return true;
            }

            return true;
        }

        var page = BuildNotePage(context.Object, state, layout);
        var local = new CPoint(context.Position.X - layout.NoteArea.X, context.Position.Y - layout.NoteArea.Y);
        try
        {
            return renderer.OnPointer(new WidgetPointerContext
            {
                Phase = phase,
                Position = local,
                LogicalSize = new CSize(layout.NoteArea.Width, layout.NoteArea.Height),
                Object = page,
                Host = foreignHost,
                IsPrimary = context.IsPrimary,
                ClickCount = context.ClickCount,
                WheelDelta = context.WheelDelta
            });
        }
        catch
        {
            return true;
        }
    }

    private bool ForwardNoteKey(WidgetKeyContext context, CalculatorSurfaceState state, CalculatorSurfaceLayout layout)
    {
        var renderer = Ui?.FindWidget(CalculatorCommands.MathNotesWidgetType);
        var foreignHost = ResolveForeignHost(context.Host, CalculatorCommands.MathNotesWidgetType);
        if (renderer is null || foreignHost is null)
        {
            return false;
        }

        var page = BuildNotePage(context.Object, state, layout);
        try
        {
            return renderer.OnKey(new WidgetKeyContext
            {
                Key = context.Key,
                Object = page,
                Host = foreignHost,
                Control = context.Control,
                Shift = context.Shift,
                Alt = context.Alt
            });
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The transient page object handed to the Math Notes renderer. It is never added to the canvas;
    /// it reuses the surface id and carries the opaque note state verbatim.
    /// </summary>
    private static WidgetObject BuildNotePage(WidgetObject surface, CalculatorSurfaceState state, CalculatorSurfaceLayout layout)
    {
        state.Note ??= new JsonObject();
        var world = layout.NoteArea;
        return new WidgetObject
        {
            Id = surface.Id,
            WidgetType = CalculatorCommands.MathNotesWidgetType,
            OwnerPluginId = CalculatorCommands.MathNotesPluginId,
            Title = surface.Title,
            Bounds = new CRect(
                surface.Bounds.X + world.X,
                surface.Bounds.Y + world.Y,
                Math.Max(1, world.Width),
                Math.Max(1, world.Height)),
            State = state.Note
        };
    }

    /// <summary>
    /// Finds the widget host of another plugin. The shell supplies one through a <c>ForPlugin</c>
    /// factory; without it the note page is rendered as a placeholder instead of borrowing the
    /// calculator's own permissions.
    /// </summary>
    private static IWidgetHost? ResolveForeignHost(IWidgetHost host, string pluginId)
    {
        if (host is null)
        {
            return null;
        }

        try
        {
            var method = host.GetType().GetMethod("ForPlugin", [typeof(string)]);
            if (method?.Invoke(host, [pluginId]) is IWidgetHost foreign)
            {
                return foreign;
            }
        }
        catch
        {
            // Fall through to "no foreign host".
        }

        return null;
    }

    // ----- drawing helpers ------------------------------------------------------------------------

    private CalculatorSurfaceLayout BuildLayout(CSize size, CalculatorSurfaceState state, PluginTheme theme)
    {
        var paged = state.Mode == CalculatorMode.Scientific && CalculatorKeypadPages.NeedsPaging(size.Width);
        var request = new CalculatorSurfaceRequest(
            Mode: state.Mode,
            ShowModeMenu: state.ShowModeMenu,
            Picker: state.Convert.Picker,
            ShowMemory: state.Mode == CalculatorMode.Scientific,
            CanRecallResult: state.Calculator.JustEvaluated,
            PageIndex: state.ScientificPage,
            PageCount: paged ? CalculatorKeypadPages.ScientificNarrow.Count : 1,
            Gap: theme.KeyGap,
            Padding: Math.Max(8, theme.WidgetPadding),
            Categories: Categories,
            CategoryId: state.Convert.CategoryId,
            PickerScroll: state.Convert.PickerScroll,
            SourceLabel: _rates.SourceLabel);
        return CalculatorSurfaceLayout.Compute(size, request);
    }

    private static void DrawHistoryIcon(DrawingContext drawing, PluginTheme theme, CRect rect)
    {
        CalculatorPaint.DrawPanel(drawing, rect, theme.Brush(theme.SurfaceAlt), 6);
        var pen = new Pen(theme.Brush(theme.TextPrimary), 1.6);
        for (var i = 0; i < 3; i++)
        {
            var y = rect.Y + (rect.Height * (0.3 + (i * 0.2)));
            drawing.DrawLine(pen, new Point(rect.X + 6, y), new Point(rect.Right - 6, y));
        }
    }

    private string BuildStatusText(CalculatorSurfaceState state, CalculatorSurfaceLayout layout) => state.Mode switch
    {
        CalculatorMode.Scientific => $"{state.Calculator.AngleLabel}{(state.Calculator.SecondActive ? " · 2nd" : string.Empty)}{(state.Calculator.HasMemory ? $" · M {state.Calculator.MemoryText}" : string.Empty)}",
        CalculatorMode.Convert => _rates.SourceLabel,
        CalculatorMode.MathNotes => "数学笔记 / Math Notes",
        _ => state.Calculator.HasMemory ? $"M {state.Calculator.MemoryText}" : string.Empty
    };

    private static void DrawMemoryRow(DrawingContext drawing, PluginTheme theme, CalculatorSurfaceLayout layout, CalculatorSurfaceState state, string? pressed)
    {
        var hasMemory = state.Calculator.HasMemory;
        CalculatorPaint.DrawTextIn(
            drawing,
            hasMemory ? $"M {state.Calculator.MemoryText}" : "M",
            layout.MemoryIndicator,
            11,
            theme.Brush(hasMemory ? theme.Accent : theme.TextSecondary),
            CalculatorPaint.TextAlign.Left,
            bold: hasMemory);

        DrawChip(drawing, theme, layout.MemoryClear, "MC", string.Equals(pressed, "ac", StringComparison.Ordinal), false);
        DrawChip(drawing, theme, layout.MemoryAdd, "M+", false, false);
        DrawChip(drawing, theme, layout.MemorySubtract, "M−", false, false);
        DrawChip(drawing, theme, layout.MemoryRecall, "MR", false, hasMemory);
    }

    private static void DrawChip(DrawingContext drawing, PluginTheme theme, CRect rect, string label, bool pressed, bool active)
    {
        var fill = active ? theme.Accent : pressed ? CalculatorPaint.Lighten(theme.SurfaceRaised, 0.3) : theme.SurfaceRaised;
        CalculatorPaint.DrawPanel(drawing, rect, theme.Brush(fill), 8);
        CalculatorPaint.DrawTextIn(
            drawing,
            label,
            rect,
            11,
            theme.Brush(active ? theme.AccentText : theme.TextPrimary),
            CalculatorPaint.TextAlign.Center,
            bold: active);
    }

    private static void DrawPickerChip(DrawingContext drawing, PluginTheme theme, CRect rect, string label, bool active)
    {
        CalculatorPaint.DrawPanel(drawing, rect, theme.Brush(active ? theme.Accent : theme.SurfaceRaised), 8);
        CalculatorPaint.DrawTextIn(
            drawing,
            label,
            rect,
            10.5,
            theme.Brush(active ? theme.AccentText : theme.TextPrimary),
            CalculatorPaint.TextAlign.Center,
            bold: active,
            maxSize: 11);
    }

    private void DrawConvertDisplay(DrawingContext drawing, PluginTheme theme, CalculatorSurfaceLayout layout, CalculatorSurfaceState state)
    {
        var display = CalculatorConvertFormatter.Build(state.Calculator.IsError ? null : state.Calculator.LastResult, state.Convert, _converter, _rates);
        var primaryBrush = theme.Brush(state.Calculator.IsError ? theme.Danger : theme.TextPrimary);
        var primaryText = state.Calculator.IsError
            ? "Error"
            : display.PrimaryText.Length > 0 ? display.PrimaryText : state.Calculator.DisplayText;
        CalculatorPaint.DrawTextIn(
            drawing,
            primaryText,
            layout.ConvertPrimaryRow,
            Math.Clamp(layout.ConvertPrimaryRow.Height * 0.62, 16, 36),
            primaryBrush,
            CalculatorPaint.TextAlign.Right,
            bold: true,
            maxSize: Math.Clamp(layout.ConvertPrimaryRow.Height * 0.9, 20, 54));

        var secondary = display.Error is { Length: > 0 }
            ? display.Error
            : state.Calculator.IsError ? string.Empty : display.SecondaryText;
        var secondaryArea = layout.ConvertSecondaryRow;
        if (layout.ConvertSwapButton.Width > 0)
        {
            secondaryArea = new CRect(secondaryArea.X, secondaryArea.Y, Math.Max(10, layout.ConvertSwapButton.X - secondaryArea.X - 6), secondaryArea.Height);
        }

        CalculatorPaint.DrawTextIn(
            drawing,
            secondary,
            secondaryArea,
            Math.Clamp(secondaryArea.Height * 0.6, 11, 22),
            theme.Brush(secondary.Length > 0 && display.Error is not null ? theme.Danger : theme.TextSecondary),
            CalculatorPaint.TextAlign.Right,
            maxSize: Math.Clamp(secondaryArea.Height * 0.9, 13, 28));

        if (layout.ConvertSwapButton.Width > 0)
        {
            CalculatorPaint.DrawPanel(drawing, layout.ConvertSwapButton, theme.Brush(theme.SurfaceRaised), 8);
            CalculatorPaint.DrawTextIn(drawing, "⇄", layout.ConvertSwapButton, Math.Clamp(layout.ConvertSwapButton.Height * 0.6, 12, 20), theme.Brush(theme.Accent), CalculatorPaint.TextAlign.Center, bold: true);
        }
    }

    private static void DrawConvertChrome(DrawingContext drawing, PluginTheme theme, CalculatorSurfaceLayout layout, CalculatorSurfaceState state)
    {
        CalculatorPaint.DrawPanel(drawing, layout.ConvertCategoryButton, theme.Brush(theme.SurfaceRaised), 8);
        var categoryLabel = state.Convert.CategoryId;
        CalculatorPaint.DrawTextIn(drawing, $"{categoryLabel} ▾", layout.ConvertCategoryButton, 11, theme.Brush(theme.TextPrimary), CalculatorPaint.TextAlign.Center, bold: true);
    }

    private static void DrawModeMenu(DrawingContext drawing, PluginTheme theme, CalculatorSurfaceLayout layout, CalculatorSurfaceState state)
    {
        foreach (var (mode, rect) in layout.ModeItems)
        {
            var active = mode == state.Mode;
            CalculatorPaint.DrawPanel(drawing, rect, theme.Brush(active ? theme.Accent : theme.SurfaceRaised), 8);
            CalculatorPaint.DrawTextIn(
                drawing,
                CalculatorModes.Chip(mode),
                rect,
                12,
                theme.Brush(active ? theme.AccentText : theme.TextPrimary),
                CalculatorPaint.TextAlign.Left,
                bold: active);
        }
    }

    private void DrawPicker(DrawingContext drawing, PluginTheme theme, CalculatorSurfaceLayout layout, CalculatorSurfaceState state)
    {
        CalculatorPaint.DrawPanel(drawing, layout.PickerArea, theme.Brush(theme.SurfaceAlt), 14);
        CalculatorPaint.DrawTextIn(
            drawing,
            state.Convert.Picker switch
            {
                "categories" => "类别 / Category",
                "primary" => $"活动单位 / Active unit · {state.Convert.PrimaryUnit}",
                "secondary" => $"换算单位 / Convert to · {state.Convert.SecondaryUnit}",
                _ => "单位 / Unit"
            },
            new CRect(layout.PickerArea.X + 8, layout.PickerArea.Y + 6, Math.Max(10, layout.PickerCloseButton.X - layout.PickerArea.X - 12), 22),
            11,
            theme.Brush(theme.TextPrimary),
            CalculatorPaint.TextAlign.Left,
            bold: true);

        CalculatorPaint.DrawPanel(drawing, layout.PickerCloseButton, theme.Brush(theme.SurfaceRaised), 8);
        CalculatorPaint.DrawTextIn(drawing, "✕", layout.PickerCloseButton, 12, theme.Brush(theme.TextPrimary), CalculatorPaint.TextAlign.Center, bold: true);

        if (layout.PickerRatesToggle.Height > 0)
        {
            var daily = _rates.Source == CurrencyRateSource.Daily;
            DrawPickerChip(drawing, theme, layout.PickerRatesToggle, daily ? "每日 / daily" : "手动 / manual", daily);
            DrawPickerChip(drawing, theme, layout.PickerRatesRefresh, "刷新", false);
            DrawPickerChip(drawing, theme, layout.PickerRatesEdit, "编辑", false);
        }

        if (layout.PickerUnits.Count > 0)
        {
            foreach (var (unit, rect) in layout.PickerUnits)
            {
                var active = string.Equals(state.Convert.PrimaryUnit, unit.Symbol, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(state.Convert.SecondaryUnit, unit.Symbol, StringComparison.OrdinalIgnoreCase);
                var fill = active ? theme.Accent : theme.SurfaceRaised;
                CalculatorPaint.DrawPanel(drawing, rect, theme.Brush(fill), 8);
                var label = string.IsNullOrWhiteSpace(unit.Symbol) ? unit.Name : $"{unit.Symbol}";
                CalculatorPaint.DrawTextIn(drawing, label, rect, 12, theme.Brush(active ? theme.AccentText : theme.TextPrimary), CalculatorPaint.TextAlign.Center, bold: active);
            }
        }
        else
        {
            foreach (var (category, rect) in layout.PickerCategories)
            {
                var active = string.Equals(state.Convert.CategoryId, category.Id, StringComparison.OrdinalIgnoreCase);
                CalculatorPaint.DrawPanel(drawing, rect, theme.Brush(active ? theme.Accent : theme.SurfaceRaised), 8);
                CalculatorPaint.DrawTextIn(drawing, $"{category.NameZh} {category.Name}", rect, 11, theme.Brush(active ? theme.AccentText : theme.TextPrimary), CalculatorPaint.TextAlign.Center, bold: active);
            }
        }

        if (layout.PickerHasMore)
        {
            CalculatorPaint.DrawTextIn(
                drawing,
                "更多… 滚动 / scroll for more",
                new CRect(layout.PickerArea.X + 6, layout.PickerArea.Bottom - 16, layout.PickerArea.Width - 12, 14),
                9.5,
                theme.Brush(theme.TextSecondary),
                CalculatorPaint.TextAlign.Right);
        }
    }

    private static string LabelFor(CalculatorKey key, CalculatorSurfaceState state) => key.Kind switch
    {
        CalculatorKeyKind.Clear => state.Calculator.ClearLabel,
        CalculatorKeyKind.Angle => state.Calculator.AngleLabel,
        CalculatorKeyKind.Second => "2nd",
        CalculatorKeyKind.Page => key.Label,
        _ => state.Calculator.SecondActive ? key.SecondLabel ?? key.Label : key.Label
    };

    // ----- state helpers --------------------------------------------------------------------------

    private static void Save(WidgetObject target, CalculatorSurfaceState state)
    {
        target.State ??= new JsonObject();
        state.Save(target.State);
    }

    private static string Encode(CalculatorSurfaceAction action) =>
        action.Kind == CalculatorSurfaceActionKind.SetMode
            ? $"{action.Kind}:{CalculatorModes.Id(action.Mode)}"
            : action.Id is null ? action.Kind.ToString() : $"{action.Kind}:{action.Id}";

    private static CalculatorSurfaceAction Decode(string encoded)
    {
        var separator = encoded.IndexOf(':');
        var kindText = separator < 0 ? encoded : encoded[..separator];
        var id = separator < 0 ? null : encoded[(separator + 1)..];
        if (!Enum.TryParse<CalculatorSurfaceActionKind>(kindText, out var kind))
        {
            return new CalculatorSurfaceAction(CalculatorSurfaceActionKind.None);
        }

        if (kind == CalculatorSurfaceActionKind.SetMode)
        {
            CalculatorModes.TryParse(id, out var mode);
            return new CalculatorSurfaceAction(kind, null, mode);
        }

        return new CalculatorSurfaceAction(kind, id);
    }

    private static bool IsPopupAction(CalculatorSurfaceActionKind kind) => kind is
        CalculatorSurfaceActionKind.PickPrimaryUnit or
        CalculatorSurfaceActionKind.PickCategory or
        CalculatorSurfaceActionKind.ClosePicker or
        CalculatorSurfaceActionKind.SetMode or
        CalculatorSurfaceActionKind.ToggleRates or
        CalculatorSurfaceActionKind.RefreshRates;

    /// <summary>Chrome actions stay available while a delegated note page owns the content area.</summary>
    private static bool IsChromeAction(CalculatorSurfaceActionKind kind) => kind is
        CalculatorSurfaceActionKind.ToggleModeMenu or
        CalculatorSurfaceActionKind.SetMode or
        CalculatorSurfaceActionKind.OpenHistory;

    private static string? ReadPressed(WidgetObject target) =>
        target.State is { } state && state.TryGetPropertyValue("pressed", out var node) && node is not null ? node.ToString() : null;

    private static void WritePressed(WidgetObject target, string? value)
    {
        target.State ??= new JsonObject();
        target.State["pressed"] = value;
    }
}
