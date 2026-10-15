namespace Calcpad.Core.Hosting;

/// <summary>
/// Knobs the plugin host reads at start-up. Implemented by <see cref="PluginHostSettings"/>, and
/// directly by the frozen <see cref="PluginHostOptions"/> record through the overloads on
/// <see cref="PluginHost"/>.
/// </summary>
public interface IPluginHostOptions
{
    /// <summary>Directory scanned for external plugin assemblies.</summary>
    string PluginsDirectory { get; }

    /// <summary>
    /// False on platforms where plugins ship inside the application package (Android). When false the
    /// plugin directory is never scanned and only built-in plugins load.
    /// </summary>
    bool AllowExternalPlugins { get; }

    /// <summary>When true every plugin starts disabled regardless of its persisted state.</summary>
    bool StartDisabled { get; }
}

/// <summary>Default <see cref="IPluginHostOptions"/> implementation.</summary>
public sealed record PluginHostSettings(
    string PluginsDirectory,
    bool AllowExternalPlugins = true,
    bool StartDisabled = false) : IPluginHostOptions
{
    /// <summary>Creates settings from the frozen <see cref="PluginHostOptions"/> contract record.</summary>
    /// <param name="options">Contract record supplied by the platform head.</param>
    public PluginHostSettings(PluginHostOptions options)
        : this(
            (options ?? throw new ArgumentNullException(nameof(options))).PluginsDirectory,
            options.AllowExternalPlugins,
            options.StartDisabled)
    {
    }

    /// <summary>Converts these settings into the frozen contract record.</summary>
    public PluginHostOptions ToOptions() => new(PluginsDirectory, AllowExternalPlugins, StartDisabled);
}
