using Calcpad.Core.Plugins;

namespace Calcpad.App.Services;

/// <summary>
/// Appends plugin diagnostics to a rolling log file. Kept deliberately simple and crash proof:
/// logging must never take the application down or leak between plugins beyond the id prefix.
/// </summary>
public sealed class PluginLogger : IPluginLogger
{
    private const long MaxBytes = 2 * 1024 * 1024;

    private readonly string _directory;
    private readonly string _pluginId;
    private readonly object _gate = new();

    public PluginLogger(string directory, string pluginId)
    {
        _directory = directory;
        _pluginId = pluginId;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            // Logging is best effort.
        }
    }

    public void Debug(string message) => Write("DEBUG", message, null);

    public void Info(string message) => Write("INFO ", message, null);

    public void Warn(string message, Exception? error = null) => Write("WARN ", message, error);

    public void Error(string message, Exception? error = null) => Write("ERROR", message, error);

    private void Write(string level, string message, Exception? error)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{_pluginId}] {message}";
        if (error is not null)
        {
            line += Environment.NewLine + error;
        }

        try
        {
            lock (_gate)
            {
                var path = Path.Combine(_directory, "calcpad.log");
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    var backup = Path.Combine(_directory, "calcpad.1.log");
                    File.Delete(backup);
                    File.Move(path, backup);
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // Never throw from a logger.
        }

        System.Diagnostics.Debug.WriteLine(line);
    }
}

/// <summary>Creates one logger per plugin, all writing into the same rolling file.</summary>
public sealed class PluginLoggerFactory
{
    private readonly string _directory;

    public PluginLoggerFactory(string dataDirectory) =>
        _directory = Path.Combine(dataDirectory, "logs");

    public IPluginLogger Create(string pluginId) => new PluginLogger(_directory, pluginId);
}
