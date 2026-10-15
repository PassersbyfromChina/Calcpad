namespace Calcpad.Core.Canvas.Engine;

/// <summary>
/// Minimal logging seam used by the canvas engine. The shell supplies its own implementation so
/// engine and plugin failures end up in the application log instead of on the UI thread.
/// </summary>
public interface ICanvasEngineLog
{
    /// <summary>Writes a verbose diagnostic message.</summary>
    void Debug(string message);

    /// <summary>Writes an informational message.</summary>
    void Info(string message);

    /// <summary>Writes a recoverable failure.</summary>
    void Warn(string message, Exception? error = null);

    /// <summary>Writes a failure that the caller could not recover from.</summary>
    void Error(string message, Exception? error = null);
}

/// <summary>Log implementation that discards everything. Used when the host supplies none.</summary>
public sealed class NullCanvasEngineLog : ICanvasEngineLog
{
    /// <summary>The shared, stateless instance.</summary>
    public static NullCanvasEngineLog Instance { get; } = new();

    private NullCanvasEngineLog()
    {
    }

    /// <inheritdoc />
    public void Debug(string message)
    {
    }

    /// <inheritdoc />
    public void Info(string message)
    {
    }

    /// <inheritdoc />
    public void Warn(string message, Exception? error = null)
    {
    }

    /// <inheritdoc />
    public void Error(string message, Exception? error = null)
    {
    }
}

/// <summary>Forwards engine log messages to caller supplied delegates.</summary>
public sealed class DelegateCanvasEngineLog : ICanvasEngineLog
{
    private readonly Action<string>? _debug;
    private readonly Action<string>? _info;
    private readonly Action<string, Exception?>? _warn;
    private readonly Action<string, Exception?>? _error;

    /// <summary>Creates a log from optional delegates; missing delegates are ignored.</summary>
    public DelegateCanvasEngineLog(
        Action<string>? debug = null,
        Action<string>? info = null,
        Action<string, Exception?>? warn = null,
        Action<string, Exception?>? error = null)
    {
        _debug = debug;
        _info = info;
        _warn = warn;
        _error = error;
    }

    /// <inheritdoc />
    public void Debug(string message) => _debug?.Invoke(message);

    /// <inheritdoc />
    public void Info(string message) => _info?.Invoke(message);

    /// <inheritdoc />
    public void Warn(string message, Exception? error = null) => _warn?.Invoke(message, error);

    /// <inheritdoc />
    public void Error(string message, Exception? error = null) => _error?.Invoke(message, error);
}
