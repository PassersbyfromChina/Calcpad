using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using Calcpad.Core.Canvas;
using Calcpad.Plugin.Handwriting.Solve;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.Handwriting.Ui;

/// <summary>What the solve bar asks the plugin to do. Implemented by the plugin itself.</summary>
public interface ISolveBarActions
{
    /// <summary>Recognises the bound ink object and solves it (or asks the user when unsure).</summary>
    ValueTask SolveAsync(string barObjectId, CancellationToken ct);

    /// <summary>Uses the <paramref name="candidateIndex"/>-th candidate as the expression and solves it.</summary>
    ValueTask ChooseAsync(string barObjectId, int candidateIndex, CancellationToken ct);

    /// <summary>Taps a number: hand it to the math notes slider, or degrade to editing.</summary>
    ValueTask AdjustAsync(string barObjectId, int numberIndex, CancellationToken ct);

    /// <summary>Edit the recognised expression by hand, then solve again.</summary>
    ValueTask EditAsync(string barObjectId, CancellationToken ct);

    /// <summary>Delete the rendered answer (never the ink).</summary>
    ValueTask DeleteAsync(string barObjectId, CancellationToken ct);

    /// <summary>Hide the bar.</summary>
    ValueTask CloseAsync(string barObjectId, CancellationToken ct);
}

/// <summary>
/// The floating Solve bar: the "Solve" entry point that appears next to handwriting, shows the answer
/// inline in a handwriting-like face, lists candidates when the recogniser refuses to guess, and turns
/// a tap on a number into a slider request.
/// <para>
/// It is a canvas widget on purpose: widgets are the surface the shell already gives plugins for
/// on-canvas UI, and its host is the one place that can run another plugin's command.
/// </para>
/// </summary>
public sealed class SolveBarWidget : IWidgetRenderer
{
    /// <summary>Widget type contributed by the handwriting plugin.</summary>
    public const string WidgetTypeId = "handwriting.solvebar";

    /// <summary>Key of the solve state inside <see cref="WidgetObject.State"/>.</summary>
    public const string StateKey = "solve";

    private readonly ISolveBarActions _actions;

    private IWidgetHost? _host;

    /// <summary>Creates the renderer.</summary>
    public SolveBarWidget(ISolveBarActions actions) =>
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));

    /// <inheritdoc />
    public string WidgetType => WidgetTypeId;

    /// <summary>Host that was attached, exposed for diagnostics.</summary>
    public IWidgetHost? AttachedHost => _host;

    /// <inheritdoc />
    public CSize Measure(WidgetMeasureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SolveBarLayout.Measure(ReadState(context.Object));
    }

    /// <inheritdoc />
    public void Render(WidgetRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var state = ReadState(context.Object);
        var zoom = context.Zoom <= 0 ? 1 : context.Zoom;
        var theme = context.Theme;
        var surface = new SolidColorBrush(theme.SurfaceRaised, 0.97);
        var accent = new SolidColorBrush(theme.Accent);
        var textBrush = new SolidColorBrush(theme.TextPrimary);
        var secondary = new SolidColorBrush(theme.TextSecondary);
        var linePen = new Pen(new SolidColorBrush(theme.GridLine), 1 * zoom);

        // The bar itself, with a warm edge so it reads as "the answer strip".
        var bounds = new Rect(context.Bounds.X, context.Bounds.Y, context.Bounds.Width, context.Bounds.Height);
        context.Drawing.DrawRectangle(surface, new Pen(accent, 1.2 * zoom), bounds, 12 * zoom, 12 * zoom);

        double X(double local) => context.Bounds.X + (local * zoom);
        double Y(double local) => context.Bounds.Y + (local * zoom);

        // Headline: "1+2 = 3", drawn in the handwriting-ish face when the answer exists.
        var headline = state.Status switch
        {
            SolveStatus.SolvedInNotes or SolveStatus.SolvedInNotesUnbound or SolveStatus.SolvedLocally =>
                $"{state.Expression} = {state.Result}",
            SolveStatus.NeedsChoice => "选择算式 / choose the expression",
            SolveStatus.Failed => state.Message,
            _ => "手写求解 / Solve"
        };

        var headlineBrush = state.Status is SolveStatus.SolvedInNotes or SolveStatus.SolvedInNotesUnbound or SolveStatus.SolvedLocally
            ? accent
            : textBrush;
        var headlineTypeface = state.Result.Length > 0
            ? new Typeface(HandwritingStyle.FontFamily, FontStyle.Italic)
            : Typeface.Default;
        Draw(context, headline, X(SolveBarLayout.Padding + 4), Y(SolveBarLayout.Padding - 2), 14, headlineBrush, headlineTypeface);

        // Buttons.
        var primaryWidth = Math.Clamp(context.Bounds.Width / zoom * 0.34, 96, 140);
        var top = SolveBarLayout.Padding;
        var primary = new Rect(X(SolveBarLayout.Padding), Y(top), primaryWidth * zoom, SolveBarLayout.ButtonHeight * zoom);
        var primaryLabel = state.Status == SolveStatus.NeedsChoice ? "重新识别" : "求解 Solve";
        DrawButton(context, primary, primaryLabel, accent, new SolidColorBrush(theme.AccentText), zoom);

        var x = SolveBarLayout.Padding + primaryWidth + SolveBarLayout.Gap;
        if (state.Numbers.Count > 0 && state.Result.Length > 0)
        {
            for (var i = 0; i < state.Numbers.Count && i < 5; i++)
            {
                var chip = new Rect(X(x), Y(top), 52 * zoom, SolveBarLayout.ButtonHeight * zoom);
                DrawButton(context, chip, state.Numbers[i], new SolidColorBrush(theme.SurfaceAlt), textBrush, zoom);
                x += 52 + SolveBarLayout.Gap;
            }
        }

        var edit = new Rect(X(x), Y(top), SolveBarLayout.NarrowButtonWidth * zoom, SolveBarLayout.ButtonHeight * zoom);
        DrawButton(context, edit, "编辑", new SolidColorBrush(theme.SurfaceAlt), textBrush, zoom);
        x += SolveBarLayout.NarrowButtonWidth + SolveBarLayout.Gap;

        var delete = new Rect(X(x), Y(top), SolveBarLayout.NarrowButtonWidth * zoom, SolveBarLayout.ButtonHeight * zoom);
        DrawButton(context, delete, "删除结果", new SolidColorBrush(theme.SurfaceAlt), new SolidColorBrush(theme.Danger), zoom);
        x += SolveBarLayout.NarrowButtonWidth + SolveBarLayout.Gap;

        var close = new Rect(X(x), Y(top), 40 * zoom, SolveBarLayout.ButtonHeight * zoom);
        DrawButton(context, close, "×", new SolidColorBrush(theme.SurfaceAlt), secondary, zoom);

        // Candidate rows.
        var rowTop = top + SolveBarLayout.ButtonHeight + SolveBarLayout.Gap;
        if (state.Status == SolveStatus.NeedsChoice)
        {
            for (var i = 0; i < state.Candidates.Count; i++)
            {
                var candidate = state.Candidates[i];
                var row = new Rect(X(SolveBarLayout.Padding), Y(rowTop), (context.Bounds.Width / zoom) - (SolveBarLayout.Padding * 2), SolveBarLayout.CandidateHeight * zoom);
                context.Drawing.DrawRectangle(new SolidColorBrush(theme.SurfaceAlt, 0.55), null, row, 8 * zoom, 8 * zoom);
                Draw(context, $"{candidate.Text}    {candidate.Confidence:P0}", row.X + (8 * zoom), row.Y + (4 * zoom), 13, textBrush, Typeface.Default);
                rowTop += SolveBarLayout.CandidateHeight + SolveBarLayout.Gap;
            }
        }

        // Honest status line (degradation notices live here).
        if (state.Message.Length > 0)
        {
            Draw(context, Truncate(state.Message, 96), X(SolveBarLayout.Padding), Y(rowTop + 2), 11.5, secondary, Typeface.Default);
        }
    }

    /// <inheritdoc />
    public bool OnPointer(WidgetPointerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Phase != WidgetPointerPhase.Down || !context.IsPrimary)
        {
            return false;
        }

        var state = ReadState(context.Object);
        var hit = SolveBarLayout.HitTest(context.Position, context.LogicalSize, state);
        if (hit.Kind == SolveBarHit.None)
        {
            return false;
        }

        context.Handled = true;
        var barId = context.Object.Id;
        switch (hit.Kind)
        {
            case SolveBarHit.Solve or SolveBarHit.Resolve:
                _ = RunAsync(() => _actions.SolveAsync(barId, CancellationToken.None));
                break;
            case SolveBarHit.Candidate:
                _ = RunAsync(() => _actions.ChooseAsync(barId, hit.Index, CancellationToken.None));
                break;
            case SolveBarHit.AdjustNumber:
                _ = RunAsync(() => _actions.AdjustAsync(barId, hit.Index, CancellationToken.None));
                break;
            case SolveBarHit.Edit:
                _ = RunAsync(() => _actions.EditAsync(barId, CancellationToken.None));
                break;
            case SolveBarHit.Delete:
                _ = RunAsync(() => _actions.DeleteAsync(barId, CancellationToken.None));
                break;
            case SolveBarHit.Close:
                _ = RunAsync(() => _actions.CloseAsync(barId, CancellationToken.None));
                break;
        }

        return true;
    }

    /// <inheritdoc />
    public bool OnKey(WidgetKeyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var barId = context.Object.Id;
        switch (context.Key)
        {
            case "Return" or "Enter":
                context.Handled = true;
                _ = RunAsync(() => _actions.SolveAsync(barId, CancellationToken.None));
                return true;

            case "Back" or "BackSpace" or "Delete":
                context.Handled = true;
                _ = RunAsync(() => _actions.DeleteAsync(barId, CancellationToken.None));
                return true;

            case "Escape":
                context.Handled = true;
                _ = RunAsync(() => _actions.CloseAsync(barId, CancellationToken.None));
                return true;

            default:
                return false;
        }
    }

    /// <inheritdoc />
    public void OnAttached(WidgetObject target, IWidgetHost host)
    {
        ArgumentNullException.ThrowIfNull(target);
        _host = host;
        WriteState(target, ReadState(target));
    }

    /// <inheritdoc />
    public void OnDetached(WidgetObject target, IWidgetHost host) => _host = null;

    /// <summary>Reads the solve state stored in a bar widget.</summary>
    public static SolveState ReadState(WidgetObject? widget)
    {
        var node = widget?.State?[StateKey] as JsonObject;
        if (node is null)
        {
            return new SolveState();
        }

        var candidates = new List<SolveCandidate>();
        if (node["candidates"] is JsonArray array)
        {
            foreach (var item in array)
            {
                var text = item?["text"]?.GetValue<string>() ?? string.Empty;
                var confidence = item?["confidence"]?.GetValue<double>() ?? 0;
                if (text.Length > 0)
                {
                    candidates.Add(new SolveCandidate(text, confidence));
                }
            }
        }

        var numbers = new List<string>();
        if (node["numbers"] is JsonArray numberArray)
        {
            foreach (var item in numberArray)
            {
                var value = item?.GetValue<string>();
                if (!string.IsNullOrEmpty(value))
                {
                    numbers.Add(value);
                }
            }
        }

        return new SolveState
        {
            SourceObjectId = node["sourceObjectId"]?.GetValue<string>() ?? string.Empty,
            RecognizedText = node["recognizedText"]?.GetValue<string>() ?? string.Empty,
            Expression = node["expression"]?.GetValue<string>() ?? string.Empty,
            Result = node["result"]?.GetValue<string>() ?? string.Empty,
            Status = Enum.TryParse<SolveStatus>(node["status"]?.GetValue<string>(), out var status) ? status : SolveStatus.Idle,
            Candidates = candidates,
            Numbers = numbers,
            NotesWidgetId = node["notesWidgetId"]?.GetValue<string>(),
            BoundToInk = node["boundToInk"]?.GetValue<bool>() ?? false,
            LocalResultObjectId = node["localResultObjectId"]?.GetValue<string>(),
            SliderAvailable = node["sliderAvailable"]?.GetValue<bool>() ?? false,
            Message = node["message"]?.GetValue<string>() ?? string.Empty,
            InkPreserved = node["inkPreserved"]?.GetValue<bool>() ?? true
        };
    }

    /// <summary>Writes the solve state back into a bar widget (so it survives a reload).</summary>
    public static void WriteState(WidgetObject widget, SolveState state)
    {
        ArgumentNullException.ThrowIfNull(widget);
        widget.State ??= new JsonObject();
        var candidates = new JsonArray();
        foreach (var candidate in state.Candidates)
        {
            candidates.Add(new JsonObject
            {
                ["text"] = candidate.Text,
                ["confidence"] = candidate.Confidence
            });
        }

        var numbers = new JsonArray();
        foreach (var number in state.Numbers)
        {
            numbers.Add(number);
        }

        widget.State[StateKey] = new JsonObject
        {
            ["sourceObjectId"] = state.SourceObjectId,
            ["recognizedText"] = state.RecognizedText,
            ["expression"] = state.Expression,
            ["result"] = state.Result,
            ["status"] = state.Status.ToString(),
            ["candidates"] = candidates,
            ["numbers"] = numbers,
            ["notesWidgetId"] = state.NotesWidgetId,
            ["boundToInk"] = state.BoundToInk,
            ["localResultObjectId"] = state.LocalResultObjectId,
            ["sliderAvailable"] = state.SliderAvailable,
            ["message"] = state.Message,
            ["inkPreserved"] = state.InkPreserved,
            ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };
    }

    private static void DrawButton(WidgetRenderContext context, Rect rect, string label, IBrush background, IBrush foreground, double zoom)
    {
        context.Drawing.DrawRectangle(background, null, rect, 8 * zoom, 8 * zoom);
        Draw(context, label, rect.X + (8 * zoom), rect.Y + (6 * zoom), 12.5, foreground, Typeface.Default);
    }

    private static void Draw(WidgetRenderContext context, string text, double x, double y, double fontSize, IBrush brush, Typeface typeface)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize * context.Zoom,
            brush);
        context.Drawing.DrawText(formatted, new Point(x, y));
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static async Task RunAsync(Func<ValueTask> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The shell cancelled the interaction; nothing to report.
        }
        catch (Exception)
        {
            // A widget must never surface an exception into the render loop; the plugin logs its own.
        }
    }
}
