using Avalonia.Controls;
using Calcpad.Core.Ai;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.AskAi.Ui;

/// <summary>Settings panel: local first, image opt in, and a read-only view of what the host allows.</summary>
public sealed class AskAiSettingsPanel
{
    private readonly AskAiPlugin _plugin;
    private readonly PluginTheme _theme;
    private readonly StackPanel _root = AskAiUi.VStack(12);
    private AskAiSettings _draft;

    /// <summary>Creates the panel.</summary>
    public AskAiSettingsPanel(AskAiPlugin plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _theme = plugin.Theme;
        _draft = plugin.Settings;
    }

    /// <summary>Builds the control.</summary>
    public Control Build()
    {
        Render();
        return new ScrollViewer
        {
            Content = _root,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
    }

    private void Render()
    {
        _root.Children.Clear();
        _root.Children.Add(AskAiUi.Title("问 AI 设置", _theme));

        var behaviour = AskAiUi.VStack(4);
        behaviour.Children.Add(AskAiUi.Body("行为", _theme));
        behaviour.Children.Add(AskAiUi.Toggle("本地模型优先（不联网、不弹上传确认）", _draft.PreferOnDevice, _theme, v => _draft = _draft with { PreferOnDevice = v }));
        behaviour.Children.Add(AskAiUi.Toggle("默认把圈选图像一起发送（可随时取消勾选）", _draft.IncludeImageByDefault, _theme, v => _draft = _draft with { IncludeImageByDefault = v }));
        behaviour.Children.Add(AskAiUi.Toggle("发送前总是显示预览", _draft.AlwaysPreview, _theme, v => _draft = _draft with { AlwaysPreview = v }));
        behaviour.Children.Add(AskAiUi.Toggle("回答卡片放在圈选内容旁边", _draft.PlaceCardNearSource, _theme, v => _draft = _draft with { PlaceCardNearSource = v }));
        _root.Children.Add(AskAiUi.Card(behaviour, _theme));

        _root.Children.Add(ProvidersCard());
        _root.Children.Add(CapabilitiesCard());

        var buttons = AskAiUi.HStack(8);
        buttons.Children.Add(AskAiUi.Primary("保存设置", _theme, () => _ = SaveAsync()));
        _root.Children.Add(buttons);
    }

    private Control ProvidersCard()
    {
        var stack = AskAiUi.VStack(4);
        stack.Children.Add(AskAiUi.Body("供应商", _theme));
        var providers = _plugin.Providers;
        if (providers.Count == 0)
        {
            stack.Children.Add(AskAiUi.Body("宿主尚未提供任何供应商。", _theme, secondary: true));
        }
        else
        {
            foreach (var provider in providers)
            {
                var tag = provider.IsLocal ? "本地" : provider.SupportsVision ? "云端 · 支持图片" : "云端 · 仅文字";
                stack.Children.Add(AskAiUi.Body($"{provider.DisplayName}（{tag}）{provider.Model}", _theme));
            }
        }

        stack.Children.Add(AskAiUi.Body("目标服务、模型与 API key 由宿主设置页管理；插件只读取当前选择。", _theme, secondary: true));
        return AskAiUi.Card(stack, _theme);
    }

    private Control CapabilitiesCard()
    {
        var stack = AskAiUi.VStack(4);
        stack.Children.Add(AskAiUi.Body("宿主能力与闸门", _theme));
        var capabilities = _plugin.Capabilities;
        if (capabilities is null)
        {
            stack.Children.Add(AskAiUi.Body("宿主尚未上报 AI 能力。", _theme, secondary: true));
            return AskAiUi.Card(stack, _theme);
        }

        stack.Children.Add(AskAiUi.Body($"本地识别：{(capabilities.OnDeviceRecognition ? "可用" : "不可用")}", _theme, secondary: true));
        stack.Children.Add(AskAiUi.Body($"本地对话：{(capabilities.OnDeviceChat ? "已配置" : "未配置")}", _theme, secondary: true));
        stack.Children.Add(AskAiUi.Body($"云端：{(capabilities.CloudConfigured ? $"{capabilities.CloudProvider} / {capabilities.CloudModel}" : "未配置")}", _theme, secondary: true));
        stack.Children.Add(AskAiUi.Body($"云上传总开关：{(capabilities.UploadEnabled ? "开" : "关")}", _theme, secondary: true));
        foreach (var warning in capabilities.Warnings)
        {
            stack.Children.Add(AskAiUi.Body($"· {warning}", _theme, secondary: true));
        }

        return AskAiUi.Card(stack, _theme);
    }

    private async Task SaveAsync()
    {
        await _plugin.UpdateSettingsAsync(_draft).ConfigureAwait(true);
        Render();
    }
}
