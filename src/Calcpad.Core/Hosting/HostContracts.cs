using Calcpad.Core.Plugins;
using Calcpad.Core.Security;

namespace Calcpad.Core.Hosting;

/// <summary>Where Calcpad keeps its files. Supplied by each platform head.</summary>
public sealed record AppPaths(
    string DataDirectory,
    string DocumentsDirectory,
    string PluginsDirectory,
    string ModelsDirectory,
    string CacheDirectory,
    string DatabaseFile)
{
    public string ExportsDirectory => Path.Combine(DocumentsDirectory, "Exports");

    public void EnsureCreated()
    {
        foreach (var dir in new[] { DataDirectory, DocumentsDirectory, PluginsDirectory, ModelsDirectory, CacheDirectory, ExportsDirectory })
        {
            Directory.CreateDirectory(dir);
        }
    }
}

/// <summary>Shell supplied prompt shown when a plugin asks for a capability.</summary>
public interface IPermissionPrompt
{
    ValueTask<PermissionDecision> AskAsync(PermissionRequest request, CancellationToken ct = default);
}

/// <summary>Shell supplied review dialog shown before anything is uploaded.</summary>
public interface IEgressPrompt
{
    ValueTask<EgressDecision> ReviewAsync(EgressPreview preview, CancellationToken ct = default);
}

/// <summary>
/// Creates the mediated context handed to one plugin. The UI shell decorates the core factory to
/// add <c>IUiRegistry</c>, which is what turns a context into an <c>IPluginUiContext</c>.
/// </summary>
public interface IPluginContextFactory
{
    IPluginContext Create(IPlugin plugin);

    void Release(string pluginId);
}

public sealed record PluginHostOptions(
    string PluginsDirectory,
    bool AllowExternalPlugins = true,
    bool StartDisabled = false);
