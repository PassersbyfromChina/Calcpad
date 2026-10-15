using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Calcpad.Core.Plugins;

namespace Calcpad.Core.Hosting;

/// <summary>Something that went wrong while probing a plugin assembly.</summary>
/// <param name="Path">Assembly that was probed.</param>
/// <param name="Message">Human readable explanation.</param>
/// <param name="Error">Underlying exception, when there was one.</param>
public sealed record PluginLoadDiagnostic(string Path, string Message, Exception? Error);

/// <summary>
/// Loads external plugins from a directory into a collectible <see cref="AssemblyLoadContext"/> per
/// assembly. A plugin that cannot be loaded, or a type that cannot be constructed, is isolated and
/// reported through <see cref="Diagnostics"/> instead of breaking the host.
/// </summary>
/// <remarks>
/// A <c>calcpad-plugin.json</c> sidecar next to an assembly is read for its <c>id</c> and
/// <c>name</c> so diagnostics can name the plugin; loading itself always scans for
/// <see cref="IPlugin"/> implementations, which is why a plugin works without the sidecar.
/// </remarks>
public sealed class FolderPluginLoader : IPluginLoader, IDisposable
{
    /// <summary>Name of the optional descriptor file next to a plugin assembly.</summary>
    public const string DescriptorFileName = "calcpad-plugin.json";

    /// <summary>
    /// Assemblies that must never be loaded into a plugin context: they are either host types (which
    /// would create a second, incompatible copy of the contracts) or framework dependencies the
    /// application already provides.
    /// </summary>
    private static readonly string[] SharedAssemblyPrefixes =
    [
        "Avalonia",
        "System.",
        "Microsoft.",
        "netstandard",
        "mscorlib",
        "WindowsBase",
        "SkiaSharp",
        "HarfBuzzSharp",
        "CommunityToolkit",
        "SQLitePCLRaw",
        "SQLitePCL"
    ];

    /// <summary>
    /// Host contracts. A plugin context must always take these from the default load context, even
    /// when the host has not touched them yet, so that <c>IPlugin</c> stays a single type.
    /// </summary>
    private static readonly string[] HostOwnedAssemblies =
    [
        "Calcpad.Core",
        "Calcpad.Plugin.Sdk",
        "Calcpad.App"
    ];

    private readonly IPluginLogger? _log;
    private readonly List<PluginLoadContext> _contexts = [];
    private readonly List<PluginLoadDiagnostic> _diagnostics = [];
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Creates the loader.</summary>
    /// <param name="log">Optional logger; diagnostics are always recorded regardless.</param>
    public FolderPluginLoader(IPluginLogger? log = null)
    {
        _log = log;
    }

    /// <summary>Everything that went wrong during the last probes, newest last.</summary>
    public IReadOnlyList<PluginLoadDiagnostic> Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return _diagnostics.ToList();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IPlugin> LoadFromDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var assemblies = Directory
            .EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsSharedAssembly(Path.GetFileName(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return LoadFromAssemblies(assemblies);
    }

    /// <inheritdoc />
    public IReadOnlyList<IPlugin> LoadFromAssemblies(IEnumerable<string> assemblyPaths)
    {
        ArgumentNullException.ThrowIfNull(assemblyPaths);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var plugins = new List<IPlugin>();
        foreach (var path in assemblyPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                Report(fullPath, "Plugin assembly does not exist.", null);
                continue;
            }

            if (IsSharedAssembly(Path.GetFileName(fullPath)))
            {
                Report(fullPath, "Skipped because it is a host or framework assembly.", null);
                continue;
            }

            try
            {
                plugins.AddRange(LoadAssembly(fullPath));
            }
            catch (Exception ex)
            {
                Report(fullPath, $"Loading the assembly failed: {ex.Message}", ex);
            }
        }

        return plugins;
    }

    /// <summary>Requests unload of every collectible context created by this loader.</summary>
    public void UnloadAll()
    {
        List<PluginLoadContext> contexts;
        lock (_gate)
        {
            contexts = _contexts.ToList();
            _contexts.Clear();
        }

        foreach (var context in contexts)
        {
            try
            {
                context.Unload();
            }
            catch (Exception ex)
            {
                Report(context.AssemblyPath, "Unloading the plugin context failed.", ex);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnloadAll();
    }

    private IEnumerable<IPlugin> LoadAssembly(string path)
    {
        var descriptor = TryReadDescriptor(path);
        if (descriptor is not null)
        {
            _log?.Debug($"Probing plugin '{descriptor.Id ?? descriptor.Name ?? Path.GetFileName(path)}' at '{path}'.");
        }

        var context = new PluginLoadContext(path, collectible: true);
        lock (_gate)
        {
            _contexts.Add(context);
        }

        Assembly assembly;
        try
        {
            assembly = context.LoadFromAssemblyPath(path);
        }
        catch (Exception ex)
        {
            Report(path, $"The assembly could not be loaded: {ex.Message}", ex);
            return [];
        }

        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
            Report(path, $"Some types could not be inspected: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            Report(path, $"The assembly types could not be inspected: {ex.Message}", ex);
            return [];
        }

        var result = new List<IPlugin>();
        foreach (var type in types)
        {
            if (type is null || type.IsAbstract || type.IsInterface || !typeof(IPlugin).IsAssignableFrom(type))
            {
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                Report(path, $"Plugin type '{type.FullName}' has no public parameterless constructor.", null);
                continue;
            }

            try
            {
                if (Activator.CreateInstance(type) is IPlugin instance
                    && instance.Manifest is { Id.Length: > 0 })
                {
                    result.Add(instance);
                    _log?.Info($"Loaded plugin '{instance.Manifest.Id}' from '{path}'.");
                }
            }
            catch (Exception ex)
            {
                Report(path, $"Plugin type '{type.FullName}' could not be constructed.", ex);
            }
        }

        if (result.Count == 0)
        {
            Report(path, "No plugin type implementing IPlugin was found.", null);
        }

        return result;
    }

    private static bool IsSharedAssembly(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return IsHostOwnedAssemblyName(name)
            || SharedAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHostOwnedAssemblyName(string? name) =>
        name is not null && HostOwnedAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static PluginDescriptor? TryReadDescriptor(string assemblyPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(assemblyPath);
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            var descriptorPath = Path.Combine(directory, DescriptorFileName);
            if (!File.Exists(descriptorPath))
            {
                return null;
            }

            using var stream = File.OpenRead(descriptorPath);
            return JsonSerializer.Deserialize<PluginDescriptor>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception)
        {
            // The descriptor is optional metadata only.
            return null;
        }
    }

    private void Report(string path, string message, Exception? error)
    {
        lock (_gate)
        {
            _diagnostics.Add(new PluginLoadDiagnostic(path, message, error));
        }

        if (error is null)
        {
            _log?.Warn($"Plugin probe: {message} ({path})");
        }
        else
        {
            _log?.Error($"Plugin probe: {message} ({path})", error);
        }
    }

    /// <summary>Optional <c>calcpad-plugin.json</c> metadata.</summary>
    private sealed record PluginDescriptor(string? Id, string? Name, string? Version, string? EntryAssembly);

    /// <summary>One collectible load context rooted at a single plugin assembly.</summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string assemblyPath, bool collectible)
            : base($"Calcpad.Plugin:{Path.GetFileNameWithoutExtension(assemblyPath)}", collectible)
        {
            AssemblyPath = assemblyPath;
            _resolver = new AssemblyDependencyResolver(assemblyPath);
        }

        public string AssemblyPath { get; }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var name = assemblyName.Name;

            // Host assemblies always come from the default context so that contract types stay unique.
            var shared = name is null
                ? null
                : Default.Assemblies.FirstOrDefault(a =>
                    string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            if (shared is not null)
            {
                return shared;
            }

            if (IsHostOwnedAssemblyName(name))
            {
                // Never load a second copy of a host contract, even if the plugin folder ships one.
                try
                {
                    return Default.LoadFromAssemblyName(assemblyName);
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    return null;
                }
            }

            // A dependency shipped next to the plugin wins over anything the host provides.
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is not null)
            {
                return LoadFromAssemblyPath(path);
            }

            // Otherwise let the host resolve it (framework, Avalonia, shared runtime libraries).
            try
            {
                return Default.LoadFromAssemblyName(assemblyName);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                return null;
            }
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
