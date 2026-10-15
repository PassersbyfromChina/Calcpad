using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Calcpad.Core.Ai;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.Handwriting.Ui;

/// <summary>
/// Settings panel: languages, engine priority, confidence thresholds, upload consent, write back
/// defaults, and the local models the host found (with their paths).
/// </summary>
public sealed class HandwritingSettingsPanel
{
    private readonly HandwritingPlugin _plugin;
    private readonly PluginTheme _theme;
    private readonly StackPanel _root = UiKit.VStack(12);

    private HandwritingSettings _draft;
    private bool _chinese;
    private bool _english;
    private HandwritingEnginePreference _engine;
    private double _high;
    private double _medium;
    private double _delta;
    private bool _confirmUpload;
    private bool _keepInk;
    private bool _preferMath;
    private IReadOnlyList<LocalModelInfo> _models = [];

    /// <summary>Creates the panel.</summary>
    public HandwritingSettingsPanel(HandwritingPlugin plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _theme = plugin.Theme;
        _draft = plugin.Settings;
        _chinese = _draft.Languages.Any(l => l.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        _english = _draft.Languages.Any(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        _engine = _draft.Engine;
        _high = _draft.HighConfidenceThreshold;
        _medium = _draft.MediumConfidenceThreshold;
        _delta = _draft.AmbiguityDelta;
        _confirmUpload = _draft.ConfirmEveryUpload;
        _keepInk = _draft.KeepInkAfterRecognition;
        _preferMath = _draft.PreferMathObject;
    }

    /// <summary>Builds the control and kicks off a local model scan.</summary>
    public Control Build()
    {
        Render();
        _ = LoadModelsAsync();
        return new ScrollViewer
        {
            Content = _root,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
    }

    private async Task LoadModelsAsync()
    {
        try
        {
            _models = await _plugin.RefreshLocalModelsAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            _models = [];
        }

        Render();
    }

    private void Render()
    {
        _root.Children.Clear();
        _root.Children.Add(UiKit.Title("手写识别设置", _theme));

        // Language channels.
        var languages = UiKit.VStack(4);
        languages.Children.Add(UiKit.Body("语言通道", _theme));
        languages.Children.Add(UiKit.Toggle("中文（汉字 / 中文数字 / 单位）", _chinese, _theme, v => _chinese = v));
        languages.Children.Add(UiKit.Toggle("English (A-Z a-z)", _english, _theme, v => _english = v));
        languages.Children.Add(UiKit.Body("数字与运算符始终可用。", _theme, secondary: true));
        _root.Children.Add(UiKit.Card(languages, _theme));

        // Engine priority.
        var engine = UiKit.VStack(4);
        engine.Children.Add(UiKit.Body("引擎优先级", _theme));
        engine.Children.Add(UiKit.Picker(
            ["内置识别器（完全离线）", "优先本地模型，缺失时用内置", "允许云端（每次上传前确认）"],
            (int)_engine,
            _theme,
            index => _engine = (HandwritingEnginePreference)Math.Clamp(index, 0, 2)));
        engine.Children.Add(UiKit.Body("内置识别器不联网；本地模型指你放进 ModelsDirectory 的 .onnx 文件；云端必须逐次确认。", _theme, secondary: true));
        _root.Children.Add(UiKit.Card(engine, _theme));

        // Thresholds.
        var thresholds = UiKit.VStack(6);
        thresholds.Children.Add(UiKit.Body("置信度阈值", _theme));
        thresholds.Children.Add(SliderRow("高置信（直接采用）", _high, 0.5, 0.99, v => _high = v));
        thresholds.Children.Add(SliderRow("中置信（采用并标注）", _medium, 0.2, 0.95, v => _medium = v));
        thresholds.Children.Add(SliderRow("候选接近阈值 Δ", _delta, 0.02, 0.4, v => _delta = v));
        thresholds.Children.Add(UiKit.Body("低置信或前两名差值不超过 Δ 时，插件会停下来让你选择。", _theme, secondary: true));
        _root.Children.Add(UiKit.Card(thresholds, _theme));

        // Cloud consent and write back.
        var behaviour = UiKit.VStack(4);
        behaviour.Children.Add(UiKit.Body("上传与写回", _theme));
        behaviour.Children.Add(UiKit.Toggle("每次上传前都要我确认", _confirmUpload, _theme, v => _confirmUpload = v));
        behaviour.Children.Add(UiKit.Toggle("识别后保留原始笔迹", _keepInk, _theme, v => _keepInk = v));
        behaviour.Children.Add(UiKit.Toggle("数学表达式写入 MathObject", _preferMath, _theme, v => _preferMath = v));
        behaviour.Children.Add(UiKit.Body("即使关闭确认，宿主的上传闸门（IEgressGuard）仍然生效。", _theme, secondary: true));
        _root.Children.Add(UiKit.Card(behaviour, _theme));

        _root.Children.Add(ModelsCard());
        _root.Children.Add(CapabilitiesCard());

        var buttons = UiKit.HStack(8);
        buttons.Children.Add(UiKit.Primary("保存设置", _theme, () => _ = SaveAsync()));
        buttons.Children.Add(UiKit.Secondary("重新扫描本地模型", _theme, () => _ = LoadModelsAsync()));
        _root.Children.Add(buttons);
    }

    private Control SliderRow(string label, double value, double min, double max, Action<double> onChange)
    {
        var stack = UiKit.VStack(2);
        var text = UiKit.Body($"{label}：{value:0.00}", _theme, secondary: true);
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            TickFrequency = 0.01,
            Width = 260
        };
        slider.PropertyChanged += (_, args) =>
        {
            if (args.Property.Name != "Value")
            {
                return;
            }

            onChange(slider.Value);
            text.Text = $"{label}：{slider.Value:0.00}";
        };
        stack.Children.Add(text);
        stack.Children.Add(slider);
        return stack;
    }

    private Control ModelsCard()
    {
        var stack = UiKit.VStack(4);
        stack.Children.Add(UiKit.Body("本地模型（ModelsDirectory）", _theme));
        if (_models.Count == 0)
        {
            stack.Children.Add(UiKit.Body("未找到本地模型，已使用内置识别器。把 .onnx（可带 labels.txt）放进模型目录即可启用。", _theme, secondary: true));
        }
        else
        {
            foreach (var model in _models)
            {
                var kind = model.Kind == LocalModelKind.Ocr ? "OCR" : "LLM";
                var state = model.Available ? "可用" : "不可用";
                stack.Children.Add(UiKit.Body($"[{kind}] {model.Name} · {state} · {model.SizeBytes / 1024} KB", _theme));
                stack.Children.Add(UiKit.Body($"    {model.Path}", _theme, secondary: true));
                if (!string.IsNullOrEmpty(model.LabelsPath))
                {
                    stack.Children.Add(UiKit.Body($"    标签：{model.LabelsPath}", _theme, secondary: true));
                }
            }
        }

        return UiKit.Card(stack, _theme);
    }

    private Control CapabilitiesCard()
    {
        var stack = UiKit.VStack(4);
        stack.Children.Add(UiKit.Body("当前能力（宿主上报）", _theme));
        var capabilities = _plugin.Capabilities;
        if (capabilities is null)
        {
            stack.Children.Add(UiKit.Body("宿主尚未提供 AI 能力信息。", _theme, secondary: true));
            return UiKit.Card(stack, _theme);
        }

        stack.Children.Add(UiKit.Body($"内置识别器：{(capabilities.OnDeviceRecognition ? "可用" : "不可用")}", _theme, secondary: true));
        stack.Children.Add(UiKit.Body($"本地对话模型：{(capabilities.OnDeviceChat ? "已配置" : "未配置")}", _theme, secondary: true));
        stack.Children.Add(UiKit.Body($"云端：{(capabilities.CloudConfigured ? $"{capabilities.CloudProvider} / {capabilities.CloudModel}" : "未配置")}", _theme, secondary: true));
        stack.Children.Add(UiKit.Body($"云上传开关：{(capabilities.UploadEnabled ? "开" : "关（IEgressGuard）")}", _theme, secondary: true));
        foreach (var warning in capabilities.Warnings)
        {
            stack.Children.Add(UiKit.Body($"· {warning}", _theme, secondary: true));
        }

        return UiKit.Card(stack, _theme);
    }

    private async Task SaveAsync()
    {
        var languages = new List<string>();
        if (_chinese)
        {
            languages.Add("zh-Hans");
        }

        if (_english)
        {
            languages.Add("en");
        }

        if (languages.Count == 0)
        {
            languages.Add("en");
        }

        _draft = _draft with
        {
            Languages = languages,
            Engine = _engine,
            HighConfidenceThreshold = _high,
            MediumConfidenceThreshold = _medium,
            AmbiguityDelta = _delta,
            ConfirmEveryUpload = _confirmUpload,
            KeepInkAfterRecognition = _keepInk,
            PreferMathObject = _preferMath
        };

        await _plugin.UpdateSettingsAsync(_draft).ConfigureAwait(true);
        Render();
    }
}
