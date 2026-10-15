using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace Calcpad.Android;

/// <summary>
/// Single activity host. Configuration changes are handled by Avalonia itself so rotating the device
/// never restarts the process and never drops the canvas document.
/// </summary>
[Activity(
    Label = "Calcpad",
    Theme = "@style/CalcpadTheme",
    Icon = "@mipmap/ic_launcher",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    Exported = true,
    ConfigurationChanges =
        ConfigChanges.Orientation |
        ConfigChanges.ScreenSize |
        ConfigChanges.ScreenLayout |
        ConfigChanges.SmallestScreenSize |
        ConfigChanges.UiMode |
        ConfigChanges.Density |
        ConfigChanges.Keyboard |
        ConfigChanges.KeyboardHidden |
        ConfigChanges.Navigation)]
public class MainActivity : AvaloniaMainActivity<Calcpad.App.App>
{
    /// <inheritdoc />
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        Calcpad.App.CalcpadBootstrapper.ConfigureAndroid(builder);
        return base.CustomizeAppBuilder(builder);
    }
}
