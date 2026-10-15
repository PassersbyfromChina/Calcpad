using Calcpad.Core.Plugins;

namespace Calcpad.Core.Hosting;

/// <summary>
/// Cross platform defaults for <see cref="AppPaths"/>. Platform heads may build their own paths
/// (Android uses the app private files directory) and simply pass them to the composition root.
/// </summary>
public static class CalcpadHostEnvironment
{
    /// <summary>Product name reported to plugins.</summary>
    public const string ProductName = "Calcpad";

    private static AppPaths? _paths;

    /// <summary>Paths the process is running with, or null before the composition root set them.</summary>
    public static AppPaths? Paths
    {
        get => _paths;
        set => _paths = value;
    }

    /// <summary>True when the running platform can host on-device AI models.</summary>
    public static bool SupportsOnDeviceAi { get; set; } = true;

    /// <summary>Human readable platform name, e.g. <c>windows</c>, <c>android</c>.</summary>
    public static string PlatformName
    {
        get
        {
            if (OperatingSystem.IsAndroid())
            {
                return "android";
            }

            if (OperatingSystem.IsWindows())
            {
                return "windows";
            }

            if (OperatingSystem.IsMacOS())
            {
                return "macos";
            }

            if (OperatingSystem.IsLinux())
            {
                return "linux";
            }

            return "unknown";
        }
    }

    /// <summary>Product version taken from the assembly metadata, e.g. <c>0.0.0</c>.</summary>
    public static string Version
    {
        get
        {
            var assembly = typeof(CalcpadHostEnvironment).Assembly;
            var informational = assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            var version = assembly.GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    /// <summary>Builds the default paths for the current platform.</summary>
    /// <param name="dataDirectory">
    /// Root data directory. When null a per-platform default is used; Android has no usable default
    /// and should always pass the app private files directory.
    /// </param>
    public static AppPaths CreateDefaultPaths(string? dataDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(dataDirectory)
            ? DefaultDataDirectory()
            : Path.GetFullPath(dataDirectory);

        var documents = DefaultDocumentsDirectory(root);
        return new AppPaths(
            root,
            documents,
            Path.Combine(root, "plugins"),
            Path.Combine(root, "models"),
            Path.Combine(root, "cache"),
            Path.Combine(root, "calcpad.db"));
    }

    /// <summary>Remembers <paramref name="paths"/> and returns them, creating defaults when null.</summary>
    public static AppPaths EnsurePaths(AppPaths? paths = null, string? dataDirectory = null)
    {
        if (paths is not null)
        {
            return _paths = paths;
        }

        return _paths ??= CreateDefaultPaths(dataDirectory);
    }

    /// <summary>Builds the <see cref="AppInfo"/> record handed to plugins.</summary>
    public static AppInfo CreateAppInfo(AppPaths? paths = null)
    {
        var effective = paths ?? _paths;
        return new AppInfo(
            ProductName,
            Version,
            PlatformName,
            Environment.Version.ToString(),
            SupportsOnDeviceAi,
            effective?.DataDirectory ?? string.Empty);
    }

    private static string DefaultDataDirectory()
    {
        if (OperatingSystem.IsAndroid())
        {
            // Android must supply its sandboxed files directory; this is only a safe fallback.
            var fallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(string.IsNullOrEmpty(fallback) ? Path.GetTempPath() : fallback, ProductName);
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, ProductName);
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(HomeDirectory(), "Library", "Application Support", ProductName);
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(HomeDirectory(), ".local", "share", ProductName)
            : Path.Combine(xdg, ProductName);
    }

    private static string DefaultDocumentsDirectory(string root)
    {
        if (OperatingSystem.IsAndroid())
        {
            return Path.Combine(root, "documents");
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrEmpty(documents)
            ? Path.Combine(root, "documents")
            : Path.Combine(documents, ProductName);
    }

    private static string HomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? Path.GetTempPath() : home;
    }
}
