# CHOP

CHOP is a Windows desktop editor for layered RGBA images with editable operations and a scriptable command engine.

## Build and run

Install the .NET 10 SDK. On Windows, open `CHOP.slnx` in Visual Studio, select **CHOP Desktop** in the startup dropdown, and press **F5**. If Visual Studio has remembered a different startup project, right-click **PhotoChop** in Solution Explorer and choose **Set as Startup Project**. The `CHOP.CLI` console project requires a script argument.

You can also run these commands from the repository root.

```sh
dotnet build CHOP.slnx
dotnet test CHOP.slnx
dotnet run --project PhotoChop.csproj
```

The solution also cross-builds on Linux. Run the WPF application on Windows with the .NET 10 Desktop Runtime. The CLI and automated tests run headlessly on Windows and Linux. On Linux, Skia uses Fontconfig for locally installed fonts.

## Desktop workflow

Create a document with **File → New**. Set width and height in pixels, inches, or millimeters, and resolution in pixels/inch or pixels/cm. CHOP converts physical sizes to pixels and records the resolution in native saves and PNG exports. New documents start with **Group 1** containing **Layer 1**. Import a PNG with **File → Import PNG** to add a raster layer, or to open the PNG as a new document when no document is open.

The Layers panel lists the topmost layer first. Drop near a row edge to reorder or on the center of a group to move a layer into it. Drop onto a row outside the group to move a layer out. Click the group triangle to expand or collapse it. The eye toggles visibility and the lock prevents editing. Opacity and blend mode are above the list. Double-click a layer or group to rename it and edit its effects. Double-click a fill layer to pick its solid color. Use the row context menu to rename or edit effects. It also offers Bake Layer or Bake Group, which rasterizes the result and keeps the original name. Toggle a row’s transform control to compare against its untransformed state. The layer toolbar has buttons for adding a raster layer, creating a group, masking, and deleting. Drop a row on either add button to duplicate it, or on the trash button to delete it. **Edit** contains transform, text, and shape commands for the selected item. **Image → Adjustments** contains brightness/contrast, hue/saturation, and opacity. Use **Filter** for grayscale and Gaussian blur. Adjustment dialogs can preview changes on the canvas before you accept them.

**Mask** adds a white mask to the selected layer or group. Click the row's mask control to switch between editing the mask and editing the layer. Paint black or use Eraser to hide pixels. Paint white to reveal them. Intermediate values give partial opacity. A group mask applies to the composite of all children. Alt-click the mask button to see the grayscale mask alone. Its context menu controls visibility, deletion, baking, copying, and pasting. Merge and flatten bake a composite onto a raster layer after confirmation. Merging requires adjacent sibling layers and can change blends that depend on the backdrop. Ungroup requires a visible group with default transform, normal blending, full opacity, and no effects or mask.

Use the tools on the left and adjust brush or shape options above the canvas. Set foreground and background colors at the foot of the Tools panel with the color picker and its hue spectrum. The Selection tool supports normal, fixed-ratio, and fixed-size marquees. Drag inside an existing selection to move its outline. Shift adds to the selection and Alt subtracts. **Select → Modify** expands or contracts it, and **Select → Color Range** selects matching pixels and samples colors directly from the active document pane. With the Move tool, drag inside the selection to offset the selected raster pixels over the original source. Delete clears selected pixels. Use the **Chop** tool to trim the canvas. A gesture records a single completed command.

Drag document tabs between docked panes or outside the main window to float them. **Window** also offers document splitting, floating, minimizing, maximizing, and reopening a minimized document. Each document has its own scrollbars, zoom, and pan. Commands follow the active document. **Navigation** shows its viewport above Layers and History. Drag a History entry to reorder it or right-click to enable, disable, or move it. Commands whose prerequisites are missing after a history edit are shown as skipped. The tool pantry stays in place when the options to its right change. Use the pin control to auto-hide a docked panel and **Window** to restore it or reset the workspace. Layout and preferences are stored under the current user's local application data in `CHOP`.

Open [the editable sample](examples/phase-one.chop) from the File menu to try text, shapes, paint, groups, transparency, and a hue effect.

| Shortcut | Action |
| --- | --- |
| Ctrl+N / Ctrl+O | New / Open |
| Ctrl+S / Ctrl+Shift+S / Ctrl+Shift+E | Save / Save As / Export PNG |
| Ctrl+W | Close document |
| Ctrl+Z / Ctrl+Y | Undo / Redo |
| Ctrl+D | Deselect |
| Ctrl+G | Group selected layers |
| Delete | Clear selected raster pixels |
| X | Swap foreground and background |
| Ctrl++ / Ctrl+- or mouse wheel | Zoom |
| Ctrl+0 / Ctrl+1 | Fit / 100% physical pixels |
| Middle drag or Space + left drag | Pan |
| V / B / E / M / I / T | Move / Brush / Eraser / Selection / Eyedropper / Text |

## CLI and command replay

Export the active document's recorded commands from **Command Log → Export script**. Use **Copy selected** to copy the commands for selected rows in execution order. A complete recording includes the initial document and its inputs, including imported PNGs. A selected range or a recording with gaps needs the corresponding starting state. Clear display hides earlier log entries. Export still includes those entries. The desktop omits Save and Export commands when extracting a script so a replay can specify its own output paths.

Run the checked-in [create](examples/create-sample.json) and [edit](examples/edit-sample.json) scripts from the repository root. The command lines below use a POSIX shell or PowerShell 7.3+.

```sh
dotnet run --project CHOP.CLI -- run examples/create-sample.json 'project="generated.chop"' 'png="generated.png"'
dotnet run --project CHOP.CLI -- run examples/edit-sample.json 'input="examples/phase-one.chop"' 'project="edited.chop"' 'png="edited.png"'
```

The script is a JSON array of command objects with `name`, optional `target` (a stable layer ID), and `args`. Replace a whole value with `{"$param":"name"}` and pass `name=JSON` as a CLI argument to reuse a macro with new inputs. A native file can be opened by path or embedded bytes. A PNG can be imported by passing `{"id":"photo","name":"Photo","path":"input.png"}` to `layer.importPng`. Use a base64 `png` value in place of the path for a self-contained script. A command error exits the CLI with its command number and an error message.

## Architecture and format

- `CHOP.Core` owns documents, layer trees, effects, command validation, history, script execution, and native files. It has no Windows or GUI dependency.
- `CHOP.Rendering` implements the raster backend with SkiaSharp. Immutable source buffers and cached effect nodes keep rendered pixels separate from editable content. An effect evaluation references its input node, and the UI evaluates composites on a worker and reuses them during viewport navigation.
- `PhotoChop.csproj` builds the `CHOP.Desktop` WPF application. The desktop and `CHOP.CLI` share the command dispatcher and renderer.
- `CHOP.Tests` covers command validation, editing, rendering, native persistence, and CLI replay.

A `.chop` file is a ZIP container with a version 3 `manifest.json`, embedded `pixels/<layer-id>.rgba`, and optional mask and pixel edit resources. CHOP also opens version 1 and 2 files. The manifest specifies sRGB canvas dimensions, document ID and metadata, layer order and hierarchy, content and parameters, and resource dimensions and relative entry paths. Pixel resources use row-major straight-alpha RGBA8. Saving writes a sibling temporary file and replaces the destination after the ZIP is complete.

Phase 1 uses CPU compositing, binary pixel selections, and raster snapshots for undo. History retains up to 250 states with a 384 MiB shared raster budget, plus the latest state. Complex transformed effects must fit the renderer's image size limits. Editable text uses the bundled Liberation Sans by default. Click existing text with the Text tool or double-click its layer to edit the text, font, size, and color. Other fonts depend on what is installed on the rendering machine.
