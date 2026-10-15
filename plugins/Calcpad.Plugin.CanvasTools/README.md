# Calcpad.Plugin.CanvasTools

Built-in Calcpad plugin that owns the infinite canvas tools and the offscreen PNG rasteriser.

* Plugin id: `calcpad.canvastools`
* Permissions: `CanvasRead`, `CanvasWrite`, `ShellIntegration` (nothing else)
* Target: `net9.0`, depends only on `Calcpad.Core`, `Calcpad.Plugin.Sdk` and Avalonia

## What it contributes

| Tool id (shared `CanvasToolIds`) | Title | Command id | Shortcut |
| --- | --- | --- | --- |
| `canvas.tool.select` | Select | `calcpad.canvastools.tool.select` | `Alt+V` |
| `canvas.tool.pen` | Pen | `calcpad.canvastools.tool.pen` | `Alt+P` |
| `canvas.tool.highlighter` | Highlighter | `calcpad.canvastools.tool.highlighter` | `Alt+H` |
| `canvas.tool.eraser` | Eraser | `calcpad.canvastools.tool.eraser` | `Alt+E` |
| `canvas.tool.lasso` | Lasso select | `calcpad.canvastools.tool.lasso` | `Alt+L` |
| `canvas.tool.select.rect` | Rectangle select | `calcpad.canvastools.tool.rect` | `Alt+R` |
| `canvas.tool.text` | Text | `calcpad.canvastools.tool.text` | `Alt+T` |
| `canvas.tool.shape` | Shape | `calcpad.canvastools.tool.shape` | `Alt+U` |

Every tool also has a menu entry. Tools live under `视图/工具/<Title>` (View → Tools) and the two
insertion tools are repeated under `插入/文本` and `插入/形状` (Insert). One extra command,
`calcpad.canvastools.view.zoomFit` (menu `视图/适应画布内容`), frames the whole canvas through
`ICanvasApi.RevealAsync`.

`ExecuteCommandAsync` never touches the shell directly: it calls `ICanvasApi.ActivateToolAsync`, and
the engine forwards that to the `CanvasEngine.ToolActivator` hook the shell installs.

## How the shell must assemble it

```csharp
// 1. The canvas engine needs a tool host, otherwise ICanvasApi.ActivateToolAsync returns false.
canvasEngine.ToolActivator = (toolId, _) =>
{
    tools.SetTool(toolId);          // shell side tool state
    view.SetTool(toolId);           // InfiniteCanvasView
    return ValueTask.FromResult(true);
};

// 2. Offscreen rasterisation for handwriting recognition and ask-AI.
engine.Rasterizer = new AvaloniaCanvasRasterizer(engine, ui);
```

* `new AvaloniaCanvasRasterizer(ICanvasEngine engine, IUiRegistry ui)` is the only constructor; the
  class implements `Calcpad.Core.Canvas.ICanvasRasterizer`.
* The rasteriser renders through the same `Calcpad.Rendering.CanvasDocumentRenderer` the live canvas
  uses, so a crop always matches the screen. Grid, selection chrome and widget focus rings are not
  drawn, and `CanvasRaster.WorldRegion` is exactly the region that was requested.
* Crops are clamped to 4096 px per side and 16 M pixels in total.
* Widgets are painted with an inert host: its canvas API is real, its `IHostApi` never shows a
  dialog, never moves the camera and never throws.

## Shared renderer source

`Calcpad.Rendering.CanvasDocumentRenderer` lives in
`src/Calcpad.App/Views/Canvas/CanvasDocumentRenderer.cs` and is **linked** into this project:

```xml
<Compile Include="..\..\src\Calcpad.App\Views\Canvas\CanvasDocumentRenderer.cs"
         Link="Rendering\CanvasDocumentRenderer.cs" />
```

That keeps one rendering implementation for the live canvas and for crops, while leaving this plugin
free of any reference to `Calcpad.App` (which references the plugin). Consequence: this assembly also
contains a copy of the `Calcpad.Rendering` types, so a project that both references this project and
compiles that file sees the compiler info `CS0436` ("type conflicts with the imported type"). It is
a warning, not an error, and the source copy wins; add `CS0436` to `NoWarn` in that project if the
noise is unwanted.

## Loading

`CanvasToolsPlugin` is a plain `IPlugin`, so `FolderPluginLoader` can load it from the plugins
directory. When the shell also references this project (needed to construct
`AvaloniaCanvasRasterizer`), make sure the plugin is not initialised twice: dedupe by assembly
identity, or keep this DLL out of the folder scan and treat it as bundled (`IsCore = true`).
