using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Calcpad.Core.Ai;
using Calcpad.Plugin.Handwriting.Solve;
using Calcpad.Plugin.Sdk.Ui;

namespace Calcpad.Plugin.Handwriting.Ui;

/// <summary>
/// The recognition result panel: confidence badge, candidate picker (shown when the recogniser
/// refuses to guess), correction notes, per object checkboxes with editable text, and the actions
/// accept / undo / re-recognise / settings.
/// </summary>
public sealed class HandwritingPanel
{
    private readonly HandwritingPlugin _plugin;
    private readonly PluginTheme _theme;
    private readonly StackPanel _root = UiKit.VStack(10);
    private readonly Dictionary<string, TextBox> _editors = [];
    private readonly Dictionary<string, CheckBox> _checkboxes = [];
    private string? _chosenText;

    /// <summary>Creates the panel for a plugin instance.</summary>
    public HandwritingPanel(HandwritingPlugin plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _theme = plugin.Theme;
        _plugin.StateChanged += (_, _) => Refresh();
    }

    /// <summary>Builds the control the shell hosts.</summary>
    public Control Build()
    {
        var scroll = new ScrollViewer
        {
            Content = _root,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        Refresh();
        return scroll;
    }

    /// <summary>Rebuilds the panel from the current session.</summary>
    public void Refresh()
    {
        _root.Children.Clear();
        _editors.Clear();
        _checkboxes.Clear();

        var session = _plugin.Session;
        if (session?.Result is null)
        {
            _root.Children.Add(UiKit.Title("手写识别", _theme));
            _root.Children.Add(UiKit.Body("还没有识别结果。使用「AI / 手写转文字」菜单或 Ctrl+Shift+H 开始。", _theme, secondary: true));
            _root.Children.Add(ScopeButtons());
            return;
        }

        var result = session.Result;
        _chosenText ??= session.TopText;

        var header = UiKit.HStack(8);
        header.Children.Add(UiKit.Title("手写识别结果", _theme));
        header.Children.Add(UiKit.Badge(TierLabel(result.Tier), TierColor(result.Tier), _theme));
        header.Children.Add(UiKit.Badge($"置信度 {UiKit.Percent(result.Confidence)}", _theme.TextSecondary, _theme));
        _root.Children.Add(header);

        _root.Children.Add(UiKit.Body($"范围：{session.ScopeSummary}", _theme, secondary: true));
        _root.Children.Add(UiKit.Body($"引擎：{result.EngineName ?? result.Engine.ToString()}{(result.UsedCloud ? "（云端，已按你确认的内容发送）" : "（本地，未联网）")}", _theme, secondary: true));

        if (result.NeedsUserChoice)
        {
            _root.Children.Add(UiKit.Banner(
                "置信度低或前两名过于接近。请选择正确的候选，插件不会替你猜。",
                Color.FromArgb(60, 255, 159, 10),
                _theme));
        }

        if (!string.IsNullOrWhiteSpace(result.Diagnostics))
        {
            _root.Children.Add(UiKit.Body(result.Diagnostics!, _theme, secondary: true));
        }

        if (result.Candidates.Count > 0)
        {
            _root.Children.Add(CandidatePicker(result));
        }

        if (result.Corrections.Count > 0)
        {
            var corrections = UiKit.VStack(4);
            corrections.Children.Add(UiKit.Body("纠错说明", _theme));
            foreach (var note in result.Corrections)
            {
                corrections.Children.Add(UiKit.Body($"「{note.Original}」→「{note.Corrected}」：{note.Reason}", _theme, secondary: true));
            }

            _root.Children.Add(UiKit.Card(corrections, _theme));
        }

        _root.Children.Add(WritebackRows(session));
        _root.Children.Add(Actions(session));
        _root.Children.Add(SolveSection(session));
    }

    /// <summary>
    /// The iPadOS style Solve block: one Solve button for the ink at hand, the inline answer once it
    /// exists, number chips that hand a value to the math notes slider, and the delete action.
    /// </summary>
    private Control SolveSection(RecognitionSession session)
    {
        var stack = UiKit.VStack(6);
        stack.Children.Add(UiKit.Body("求解 / Solve（结果出现在算式旁边，不改动笔迹）", _theme));

        var targetInk = session.TargetObjectIds.FirstOrDefault();
        var solve = UiKit.Primary("求解这个算式 Solve", _theme, () => _ = _plugin.SolveAsync(targetInk));
        stack.Children.Add(solve);

        var state = _plugin.LastSolve;
        if (state is null)
        {
            stack.Children.Add(UiKit.Body("写下算式后点这里：识别 → 分级 → 行内显示结果。", _theme, secondary: true));
            return UiKit.Card(stack, _theme);
        }

        var engine = state.Engine switch
        {
            "mathnotes-solve" => "数学笔记（绑定笔迹）",
            "mathnotes-append" => "数学笔记（未绑定笔迹）",
            "local-fallback" => "本地渲染（Math Notes 不可用）",
            _ => state.Engine
        };
        stack.Children.Add(UiKit.Body($"状态：{DescribeSolveStatus(state.Status)} · 引擎：{engine}", _theme, secondary: true));
        if (state.Result.Length > 0)
        {
            var line = new TextBlock
            {
                Text = $"{state.Expression} = {state.Result}",
                FontSize = 20,
                FontStyle = FontStyle.Italic,
                FontFamily = new FontFamily(HandwritingStyle.FontFamily),
                Foreground = _theme.Brush(_theme.Accent),
                TextWrapping = TextWrapping.Wrap
            };
            stack.Children.Add(line);
        }

        if (state.Numbers.Count > 0 && state.Result.Length > 0)
        {
            var chips = UiKit.HStack(6);
            chips.Children.Add(UiKit.Body("点数字调整：", _theme, secondary: true));
            for (var i = 0; i < state.Numbers.Count; i++)
            {
                var index = i;
                chips.Children.Add(UiKit.Secondary(state.Numbers[i], _theme, () => _ = _plugin.AdjustSolveNumberAsync(string.Empty, index)));
            }

            stack.Children.Add(chips);
        }

        if (state.Status == SolveStatus.NeedsChoice && state.Candidates.Count > 0)
        {
            stack.Children.Add(UiKit.Banner("置信度不足，请选择正确的算式（插件不猜）", _theme.Accent, _theme));
            for (var i = 0; i < state.Candidates.Count; i++)
            {
                var candidate = state.Candidates[i];
                var index = i;
                stack.Children.Add(UiKit.Secondary(
                    $"用「{candidate.Text}」求解 · {UiKit.Percent(candidate.Confidence)}",
                    _theme,
                    () => _ = _plugin.ChooseFromBarAsync($"panel:{state.SourceObjectId}", index)));
            }
        }

        if (state.Message.Length > 0)
        {
            stack.Children.Add(UiKit.Body(state.Message, _theme, secondary: true));
        }

        var actions = UiKit.HStack(8);
        actions.Children.Add(UiKit.Secondary("重新求解", _theme, () => _ = _plugin.SolveAsync(state.SourceObjectId)));
        actions.Children.Add(UiKit.Secondary("删除求解结果", _theme, () => _ = _plugin.DeleteSolveResultAsync()));
        stack.Children.Add(actions);
        return UiKit.Card(stack, _theme);
    }

    private static string DescribeSolveStatus(SolveStatus status) => status switch
    {
        SolveStatus.SolvedInNotes => "已求解（数学笔记行，绑定笔迹）",
        SolveStatus.SolvedInNotesUnbound => "已求解（数学笔记行）",
        SolveStatus.SolvedLocally => "已求解（本地渲染）",
        SolveStatus.NeedsChoice => "需要你选择候选",
        SolveStatus.Failed => "求解失败",
        SolveStatus.Refused => "已拒绝",
        _ => "待求解"
    };

    private Control CandidatePicker(RecognitionResult result)
    {
        var stack = UiKit.VStack(6);
        stack.Children.Add(UiKit.Body("候选（点击选择，逐条也可在下方单独编辑）", _theme));
        for (var i = 0; i < result.Candidates.Count; i++)
        {
            var candidate = result.Candidates[i];
            var isCurrent = string.Equals(candidate.Text, _chosenText, StringComparison.Ordinal);
            var radio = new RadioButton
            {
                GroupName = "handwriting-candidates",
                IsChecked = isCurrent || (i == 0 && _chosenText is null),
                Foreground = _theme.Brush(_theme.TextPrimary),
                FontSize = 13,
                Content = $"{candidate.Text}    {UiKit.Percent(candidate.Confidence)}" +
                          (string.IsNullOrEmpty(candidate.Note) ? string.Empty : $"   · {candidate.Note}")
            };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                {
                    _chosenText = candidate.Text;
                    ApplyChoiceToEditors(session: null);
                }
            };
            stack.Children.Add(radio);
        }

        return UiKit.Card(stack, _theme);
    }

    private Control WritebackRows(RecognitionSession session)
    {
        var stack = UiKit.VStack(6);
        stack.Children.Add(UiKit.Body($"写回（{session.Writebacks.Count(w => w.Selected)}/{session.Writebacks.Count} 个对象）", _theme));
        foreach (var writeback in session.Writebacks)
        {
            var check = UiKit.Toggle($"写入 {writeback.Kind} 对象 {ShortId(writeback.ObjectId)}", writeback.Selected, _theme, value => writeback.Selected = value);
            var editor = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(writeback.Text) ? _chosenText ?? session.TopText : writeback.Text,
                FontSize = 13,
                MinWidth = 200,
                AcceptsReturn = false
            };
            editor.TextChanged += (_, _) => writeback.Text = editor.Text ?? string.Empty;

            _checkboxes[writeback.ObjectId] = check;
            _editors[writeback.ObjectId] = editor;

            var row = UiKit.VStack(4);
            row.Children.Add(check);
            row.Children.Add(editor);
            stack.Children.Add(row);
        }

        return UiKit.Card(stack, _theme);
    }

    private Control Actions(RecognitionSession session)
    {
        var buttons = UiKit.HStack(8);
        buttons.Children.Add(UiKit.Primary("接受勾选项", _theme, () => _ = _plugin.AcceptAsync()));
        buttons.Children.Add(UiKit.Secondary("全部接受", _theme, () =>
        {
            foreach (var writeback in session.Writebacks)
            {
                writeback.Selected = true;
            }

            _ = _plugin.AcceptAsync();
        }));
        buttons.Children.Add(UiKit.Secondary("撤销写回", _theme, () => _ = _plugin.UndoAsync()));
        buttons.Children.Add(UiKit.Secondary("重新识别", _theme, () => _ = _plugin.RunAsync(session.Scope, session.TargetObjectIds, null)));
        return buttons;
    }

    private Control ScopeButtons()
    {
        var stack = UiKit.VStack(8);
        stack.Children.Add(UiKit.Body("选择识别范围", _theme));
        var row = UiKit.HStack(8);
        row.Children.Add(UiKit.Primary("整画布转文字", _theme, () => _ = _plugin.RecognizeWholeCanvasAsync()));
        row.Children.Add(UiKit.Secondary("圈选部分", _theme, () => _ = _plugin.RecognizeRegionAsync()));
        row.Children.Add(UiKit.Secondary("当前选中", _theme, () => _ = _plugin.RecognizeSelectionAsync()));
        stack.Children.Add(row);
        stack.Children.Add(UiKit.Body("整画布识别默认只在本机进行；开启云端时会按对象逐个裁剪并逐个征求同意。", _theme, secondary: true));
        return stack;
    }

    private void ApplyChoiceToEditors(RecognitionSession? session)
    {
        session ??= _plugin.Session;
        if (session is null || _chosenText is null)
        {
            return;
        }

        foreach (var writeback in session.Writebacks)
        {
            writeback.Text = _chosenText;
            if (_editors.TryGetValue(writeback.ObjectId, out var editor))
            {
                editor.Text = _chosenText;
            }
        }
    }

    private static string ShortId(string id) => id.Length <= 6 ? id : id[..6];

    private static string TierLabel(RecognitionConfidenceTier tier) => tier switch
    {
        RecognitionConfidenceTier.High => "高置信",
        RecognitionConfidenceTier.Medium => "中置信",
        RecognitionConfidenceTier.Low => "低置信",
        _ => "无"
    };

    private Color TierColor(RecognitionConfidenceTier tier) => tier switch
    {
        RecognitionConfidenceTier.High => _theme.Success,
        RecognitionConfidenceTier.Medium => _theme.Accent,
        _ => _theme.Danger
    };
}
