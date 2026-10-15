using Calcpad.Core.Ai;
using Calcpad.Core.Canvas;
using Calcpad.Core.Plugins;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.Handwriting.Solve;

/// <summary>
/// Everything the solve flow needs from whoever started it: the permission checked canvas, the host
/// services (including the cross plugin command API) and the AI facade of this plugin.
/// </summary>
public interface ISolveHost
{
    /// <summary>Canvas facade of this plugin (already permission checked).</summary>
    ICanvasApi Canvas { get; }

    /// <summary>Host services: toasts, prompts, reveal and cross plugin command invocation.</summary>
    IHostApi Host { get; }

    /// <summary>Recognition facade (tiering, correction, local first).</summary>
    IAiApi Ai { get; }

    /// <summary>Logger of the owning plugin.</summary>
    IPluginLogger Log { get; }
}

/// <summary>Command ids of the Math Notes plugin, frozen by the cross plugin contract (task-5).</summary>
public static class MathNotesCommands
{
    /// <summary>Inserts text as a math notes block (or appends to <c>targetId</c>).</summary>
    public const string Append = "calcpad.math.notes.append";

    /// <summary>Inserts one normal math notes line and shows its result inline; binds <c>sourceObjectId</c>.</summary>
    public const string Solve = "calcpad.math.notes.solve";

    /// <summary>Opens the number slider of a solved line ("tap a number to adjust it").</summary>
    public const string Slider = "calcpad.math.notes.slider";

    /// <summary>Widget type of a math notes block, used to observe whether a command landed.</summary>
    public const string WidgetType = "calcpad.math.notes";

    /// <summary>Content width of a notes page in world units (frozen by math-calc).</summary>
    public const double PageWidth = 520;

    /// <summary>Line height of a notes page in world units (frozen by math-calc).</summary>
    public const double LineHeight = 34;
}

/// <summary>How a solve attempt ended.</summary>
public enum SolveStatus
{
    /// <summary>Nothing to solve yet.</summary>
    Idle,

    /// <summary>Recognition confidence was too low or two candidates were too close: the user must choose.</summary>
    NeedsChoice,

    /// <summary>The expression was inserted as a math notes line and its result is shown inline.</summary>
    SolvedInNotes,

    /// <summary>A Math Notes block received the expression through <c>append</c> (no ink binding).</summary>
    SolvedInNotesUnbound,

    /// <summary>No Math Notes command was reachable, so the plugin rendered the result itself.</summary>
    SolvedLocally,

    /// <summary>The expression could not be evaluated.</summary>
    Failed,

    /// <summary>The upload or the command was refused; nothing was inserted.</summary>
    Refused
}

/// <summary>One candidate the user can pick when the recogniser refuses to guess.</summary>
public sealed record SolveCandidate(string Text, double Confidence);

/// <summary>State of a solve session; persisted in the solve bar widget so it survives reloads.</summary>
public sealed record SolveState
{
    /// <summary>Ink object the expression was written on.</summary>
    public string SourceObjectId { get; init; } = string.Empty;

    /// <summary>Raw recognition output (before correction).</summary>
    public string RecognizedText { get; init; } = string.Empty;

    /// <summary>Expression actually solved (corrected text or the candidate the user picked).</summary>
    public string Expression { get; init; } = string.Empty;

    /// <summary>Evaluated value, empty when evaluation failed.</summary>
    public string Result { get; init; } = string.Empty;

    /// <summary>Current status.</summary>
    public SolveStatus Status { get; init; } = SolveStatus.Idle;

    /// <summary>Ranked candidates when the user has to choose.</summary>
    public IReadOnlyList<SolveCandidate> Candidates { get; init; } = [];

    /// <summary>Id of the notes widget the line landed in, when a command path worked.</summary>
    public string? NotesWidgetId { get; init; }

    /// <summary>True when the notes line carries <c>sourceObjectId</c>, so MathNotes renders it next to the ink.</summary>
    public bool BoundToInk { get; init; }

    /// <summary>Id of the result object this plugin created itself (fallback path only).</summary>
    public string? LocalResultObjectId { get; init; }

    /// <summary>True when the math notes slider is live for this line.</summary>
    public bool SliderAvailable { get; init; }

    /// <summary>User facing message, including honest degradation notices.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Number literals of the expression, in order (for "tap a number to adjust").</summary>
    public IReadOnlyList<string> Numbers { get; init; } = [];

    /// <summary>True when the ink is untouched by this plugin (it always is; kept for the audit trail).</summary>
    public bool InkPreserved { get; init; } = true;

    /// <summary>Which command path produced this state, for diagnostics and tests.</summary>
    public string Engine { get; init; } = string.Empty;
}

/// <summary>Result of a "tap a number" request.</summary>
public enum AdjustOutcome
{
    /// <summary>The Math Notes slider opened; the user drags it and the note recomputes live.</summary>
    SliderOpened,

    /// <summary>No slider available: the plugin falls back to editing the number itself.</summary>
    EditFallback,

    /// <summary>There was no number to adjust.</summary>
    NothingToAdjust,

    /// <summary>Editing was cancelled or failed.</summary>
    Cancelled
}

/// <summary>Taps the solve bar understands.</summary>
public enum SolveBarHit
{
    None,
    Solve,
    Resolve,
    Candidate,
    AdjustNumber,
    Edit,
    Delete,
    Close
}

/// <summary>A hit test result: which control, and which candidate/number index when relevant.</summary>
public readonly record struct SolveBarHitResult(SolveBarHit Kind, int Index = -1);

/// <summary>Adapts a plain plugin context to <see cref="ISolveHost"/> (menu / shortcut path).</summary>
public sealed class PluginSolveHost : ISolveHost
{
    /// <summary>Wraps a plugin context.</summary>
    public PluginSolveHost(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Canvas = context.Canvas;
        Host = context.Host;
        Ai = context.Ai;
        Log = context.Log;
    }

    /// <inheritdoc />
    public ICanvasApi Canvas { get; }

    /// <inheritdoc />
    public IHostApi Host { get; }

    /// <inheritdoc />
    public IAiApi Ai { get; }

    /// <inheritdoc />
    public IPluginLogger Log { get; }
}

/// <summary>
/// Adapts the canvas widget host to <see cref="ISolveHost"/>. The widget host exposes the same
/// <see cref="IHostApi"/> the plugin context does, so the solve bar takes exactly the same code path
/// as the menu entry.
/// </summary>
public sealed class WidgetSolveHost : ISolveHost
{
    /// <summary>Wraps an <see cref="IWidgetHost"/>.</summary>
    public WidgetSolveHost(IWidgetHost host, IAiApi ai, IPluginLogger log)
    {
        ArgumentNullException.ThrowIfNull(host);
        Canvas = host.Canvas;
        Host = host.Host;
        Ai = ai ?? throw new ArgumentNullException(nameof(ai));
        Log = log ?? new NullLogger();
    }

    /// <inheritdoc />
    public ICanvasApi Canvas { get; }

    /// <inheritdoc />
    public IHostApi Host { get; }

    /// <inheritdoc />
    public IAiApi Ai { get; }

    /// <inheritdoc />
    public IPluginLogger Log { get; }

    private sealed class NullLogger : IPluginLogger
    {
        public void Debug(string message)
        {
        }

        public void Info(string message)
        {
        }

        public void Warn(string message, Exception? error = null)
        {
        }

        public void Error(string message, Exception? error = null)
        {
        }
    }
}
