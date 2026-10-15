using Calcpad.Core.Hosting;

namespace Calcpad.Desktop;

/// <summary>
/// Builds the <see cref="AppPaths"/> the desktop head runs with:
/// <c>%APPDATA%\Calcpad</c> for data, <c>Documents\Calcpad</c> for documents, and
/// <c>calcpad.db</c> inside the data directory.
/// </summary>
public static class DesktopPaths
{
    /// <summary>Command line switch that overrides the data directory.</summary>
    public const string DataDirectorySwitch = "--data-dir";

    /// <summary>
    /// Builds the paths for this process. <c>--data-dir &lt;path&gt;</c> (or
    /// <c>--data-dir=&lt;path&gt;</c>) moves the data, plugins, models, cache and database under the
    /// given directory, which is what portable installs and the automated build use.
    /// </summary>
    /// <param name="args">Raw command line arguments.</param>
    public static AppPaths Create(IReadOnlyList<string>? args = null)
    {
        var root = ReadDataDirectory(args) ?? DefaultDataDirectory();
        root = Path.GetFullPath(root);

        var documents = DefaultDocumentsDirectory();
        return new AppPaths(
            root,
            documents,
            Path.Combine(root, "plugins"),
            Path.Combine(root, "models"),
            Path.Combine(root, "cache"),
            Path.Combine(root, "calcpad.db"));
    }

    /// <summary>Reads <c>--data-dir</c> from the command line, or null when it is absent.</summary>
    /// <param name="args">Raw command line arguments.</param>
    public static string? ReadDataDirectory(IReadOnlyList<string>? args)
    {
        if (args is null)
        {
            return null;
        }

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (arg.StartsWith(DataDirectorySwitch + "=", StringComparison.OrdinalIgnoreCase))
            {
                var inline = arg[(DataDirectorySwitch.Length + 1)..].Trim('"');
                if (!string.IsNullOrWhiteSpace(inline))
                {
                    return inline;
                }

                continue;
            }

            if (string.Equals(arg, DataDirectorySwitch, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                var next = args[i + 1]?.Trim('"');
                if (!string.IsNullOrWhiteSpace(next))
                {
                    return next;
                }
            }
        }

        return null;
    }

    /// <summary>Data directory used when the user did not override it.</summary>
    public static string DefaultDataDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(appData, "Calcpad");
    }

    /// <summary>Documents directory used for exports and saved canvases.</summary>
    public static string DefaultDocumentsDirectory()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents))
        {
            documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(documents, "Calcpad");
    }
}
