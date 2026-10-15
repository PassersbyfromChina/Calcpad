using Avalonia.Controls;
using Avalonia.Threading;
using Calcpad.Core.Hosting;
using Calcpad.Core.Plugins;
using Calcpad.Core.Security;
using Calcpad.App.Views.Dialogs;

namespace Calcpad.App.Services;

/// <summary>Bridges <see cref="IPermissionBroker"/> to the authorization dialog.</summary>
public sealed class AppPermissionPrompt : IPermissionPrompt
{
    private readonly Func<Window?> _ownerProvider;

    public AppPermissionPrompt(Func<Window?> ownerProvider) => _ownerProvider = ownerProvider;

    public async ValueTask<PermissionDecision> AskAsync(PermissionRequest request, CancellationToken ct = default)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new PermissionDialog(request);
            var owner = _ownerProvider();
            if (owner is null)
            {
                // No window yet (headless start-up): refuse rather than silently allowing.
                return PermissionDecision.Denied;
            }

            return await dialog.ShowDialog<PermissionDecision>(owner);
        });
    }
}

/// <summary>Bridges <see cref="IEgressGuard"/> to the review dialog shown before any upload.</summary>
public sealed class AppEgressPrompt : IEgressPrompt
{
    private readonly Func<Window?> _ownerProvider;

    public AppEgressPrompt(Func<Window?> ownerProvider) => _ownerProvider = ownerProvider;

    public async ValueTask<EgressDecision> ReviewAsync(EgressPreview preview, CancellationToken ct = default)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var owner = _ownerProvider();
            if (owner is null)
            {
                return EgressDecision.Blocked;
            }

            var pluginName = preview.Intent.Split('|')[0];
            var dialog = new EgressReviewDialog(pluginName, preview);
            return await dialog.ShowDialog<EgressDecision>(owner);
        });
    }
}
