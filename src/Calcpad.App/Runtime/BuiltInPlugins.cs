using Calcpad.Core.Plugins;
using Calcpad.Plugin.AskAi;
using Calcpad.Plugin.BasicCalculator;
using Calcpad.Plugin.CanvasTools;
using Calcpad.Plugin.Handwriting;
using Calcpad.Plugin.History;
using Calcpad.Plugin.MathNotes;
using Calcpad.Plugin.ScientificCalculator;
using Calcpad.Plugin.UnitConverter;

namespace Calcpad.App;

/// <summary>
/// The plugins that ship inside Calcpad. Every one of them goes through the same manifest,
/// permission and command machinery as a plugin loaded from disk, so disabling one removes its
/// menus, shortcuts, panel and canvas widgets without any shell change.
/// </summary>
public static class BuiltInPlugins
{
    public static IReadOnlyList<IPlugin> Create() =>
    [
        new CanvasToolsPlugin(),
        new BasicCalculatorPlugin(),
        new ScientificCalculatorPlugin(),
        new UnitConverterPlugin(),
        new MathNotesPlugin(),
        new HistoryPlugin(),
        new HandwritingPlugin(),
        new AskAiPlugin()
    ];
}
