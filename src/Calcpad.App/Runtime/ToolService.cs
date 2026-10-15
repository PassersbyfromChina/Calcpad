namespace Calcpad.App;

/// <summary>
/// Tracks which canvas tool is active. The shell owns this so a plugin can activate a tool
/// through the authorized canvas API without holding a reference to the view.
/// </summary>
public sealed class ToolService
{
    public string? ActiveToolId { get; private set; }

    public event EventHandler<string?>? ToolChanged;

    public void SetTool(string? toolId)
    {
        if (string.Equals(ActiveToolId, toolId, StringComparison.Ordinal))
        {
            return;
        }

        ActiveToolId = toolId;
        ToolChanged?.Invoke(this, toolId);
    }
}
