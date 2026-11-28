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

The Layers panel lists the topmost layer first. Drop near a row edge to reorder or on the center of a group to move a layer into it. Drop onto a row outside the group to move a layer out. Click the group triangle to expand or collapse it. The eye toggles visibility and the lock prevents editing. Opacity and blend mode are above the list. Double-click a layer or group to rename it and edit its effects. Double-click a fill layer to pick its solid color. Use the row context menu to rename or edit effects. It also offers Bake Layer or Bake Group, which rasterizes the result and keeps the original name. Toggle a row’s transform control to bypass its unbaked transforms, adjustments, and filters. A group toggle bypasses the group’s own shared edits while preserving each child’s transforms, adjustments, and filters. The layer toolbar has buttons for adding a raster layer, creating a group, masking, and deleting. Drop a row on either add button to duplicate it, or on the trash button to delete it. **Edit** contains transform, text, and shape commands for the selected item. **Image → Adjustments** contains brightness/contrast, hue/saturation, and opacity. Use **Filter** for grayscale and Gaussian blur. Adjustment and text dialogs preview changes on the canvas by default. Hue / Saturation uses the same hue spectrum as the color picker. Gaussian and Motion Blur are under **Filter → Blur**.

**Mask** adds a white mask to the selected layer or group. Click the row's mask control to switch between editing the mask and editing the layer. Paint black or use Eraser to hide pixels. Paint white to reveal them. Intermediate values give partial opacity. A group mask applies to the composite of all children. Alt-click the mask button to see the grayscale mask alone. Drop a layer on the mask button to add a mask. Its context menu controls visibility, deletion, baking, copying, and pasting. Merge and flatten bake a composite onto a raster layer after confirmation. Merging requires adjacent sibling layers and can change blends that depend on the backdrop. Ungroup requires a visible group with default transform, normal blending, full opacity, and no effects or mask.

Use the tools on the left and adjust brush or shape options above the canvas. Set foreground and background colors at the foot of the Tools panel with the color picker and its hue spectrum. The Selection tool supports normal, fixed-ratio, and fixed-size marquees. Drag inside an existing selection to move its outline. Shift adds to the selection and Alt subtracts. **Select → Modify** expands or contracts it, and **Select → Color Range** samples directly from the active document pane and can add matching pixels to or subtract them from the existing selection. With the Move tool, drag inside a selection to pick up pixels. They stay floating through later drags and commit when deselected or when another pixel edit needs a committed layer. The Lasso selects freeform or polygonal regions. Bucket fills connected pixels, Magic Wand selects them, and Masker paints a layer mask with its own color palette. Delete clears selected pixels. Use the **Chop** tool to trim the canvas. A gesture records a single completed command. The wheel zooms. Shift + wheel pans vertically, and Ctrl + wheel pans horizontally.

Drag document tabs between docked panes or outside the main window to float them. **Window** also offers document splitting, floating, minimizing, maximizing, and reopening a minimized document. **Image → Canvas Size** changes the bounds and anchors the artwork without scaling it. **Image → Image Size** scales the artwork and accepts percentages or physical dimensions. **Edit → Transform** contains scale, flips, fill, and selection stroke. Scale and flip affect selected raster pixels when a selection is active. Whole-layer scale and flip use canvas coordinates. Layer-level spatial operations compose into a single editable Transform property. Scale uses a nine-point anchor. Free Transform, Rotate, and Skew have interactive handles. Whole-layer operations remain editable. Each document has its own scrollbars, zoom, pan, rulers, and guides. Right-click a ruler to choose pixels, inches, or millimeters. Drag from a ruler to add a guide. Guides and marquee edges snap to each other. The View menu controls guide visibility, locking, clearing, and color. Ctrl+semicolon toggles guides and Ctrl+R toggles rulers. Commands follow the active document. **Navigation** shows its viewport above Layers and History. Drag a History entry to reorder it or right-click to enable, disable, or move it. Commands whose prerequisites are missing after a history edit are shown as skipped. The two tool sections can be docked independently. Configure Tools controls their order and visibility. Use the pin control to auto-hide a docked panel and **Window** to restore it or reset the workspace. Layout and preferences are stored under the current user's local application data in `CHOP`. Preferences include ruler units and centers and the artwork and mask palettes.

Open [the editable sample](examples/phase-one.chop) from the File menu to try text, shapes, paint, groups, transparency, and a hue effect.

**Edit** includes clipboard copy and paste for pixels and system bitmap images, full-layer Fill, and destructive Stroke. Layer and group effects include Stroke and Drop Shadow. An Effect Layer adjusts the composite of the siblings below it in the same group or at the same root tier. Bit Crush samples pixel blocks. **Image → Image Rotation** provides whole-image rotation and flips, with reset and raster bake options. Baking rasterizes artwork layers and groups while retaining top-level Effect Layers.

| Shortcut | Action |
| --- | --- |
| Ctrl+N / Ctrl+O | New / Open |
| Ctrl+S / Ctrl+Shift+S / Ctrl+Shift+E | Save / Save As / Export PNG |
| Ctrl+W | Close document |
| Ctrl+Z / Ctrl+Y | Undo / Redo |
| Ctrl+D | Deselect and commit floating pixels |
| Ctrl+C / Ctrl+V | Copy / paste pixels |
| G / W | Bucket / Magic Wand |
| Ctrl+G | Group selected layers |
| Delete | Clear selected raster pixels |
| X | Swap foreground and background |
| Ctrl++ / Ctrl+- or mouse wheel | Zoom |
| Ctrl+0 / Ctrl+1 | Fit / 100% physical pixels |
| Middle drag or Space + left drag | Pan |
| V / B / E / M / I / T | Move / Brush / Eraser / Selection / Eyedropper / Text |

## Photoshop PSD import

**File → Import → Photoshop PSD** opens a PSD as a new editable document. **Photoshop PSD (Flattened)** reads the saved composite for appearance fidelity. The flattened option requires a PSD saved with a composite. Save the imported document as a `.chop` project.

The importer preserves raster layers and groups, clipping, raster and vector masks, common Photoshop blend modes, resolution, guides, named extra channels, and native RGB, Grayscale, or CMYK samples at supported depths. It stores the source ICC profile for preservation. Photoshop text, Smart Objects, and vector shape content can use saved raster data. Unsupported adjustment layers, styles, or blend behavior are reported in import notes. The standard PSD reader supports raw, PackBits RLE, ZIP, and ZIP prediction. PSB and Photoshop multichannel mode are outside the supported format.

Format references include the [Adobe Photoshop File Format Specification](https://www.adobe.com/devnet-apps/photoshop/fileformatashtml/) and the [psd-tools PSD structures](https://github.com/psd-tools/psd-tools/tree/main/src/psd_tools/psd).

## CLI and command replay

Export the active document's recorded commands from **Command Log → Export script**. Use **Copy selected** to copy the commands for selected rows in execution order. A complete recording includes the initial document and its inputs, including imported PNGs. A selected range or a recording with gaps needs the corresponding starting state. Clear display hides earlier log entries. Export still includes those entries. **Export script with outputs** includes Save and Export as replayable commands and replaces their paths with required parameters named `output1`, `output2`, and so on. **Export script** records the edits without output commands.

Run the checked-in [create](examples/create-sample.json) and [edit](examples/edit-sample.json) scripts from the repository root. The command lines below use a POSIX shell or PowerShell 7.3+.

```sh
dotnet run --project CHOP.CLI -- run examples/create-sample.json 'project="generated.chop"' 'png="generated.png"'
dotnet run --project CHOP.CLI -- run examples/edit-sample.json 'input="examples/phase-one.chop"' 'project="edited.chop"' 'png="edited.png"'
```

The script is a JSON array of command objects with `name`, optional `target` (a stable layer ID), and `args`. Replace a whole value with `{"$param":"name"}` and pass `name=JSON` as a CLI argument to reuse a macro with new inputs. A native file can be opened by path or embedded bytes. A PNG can be imported by passing `{"id":"photo","name":"Photo","path":"input.png"}` to `layer.importPng`. Use a base64 `png` value in place of the path for a self-contained script. A command error exits the CLI with its command number and an error message.

| Action | Command and arguments |
| --- | --- |
| Import a Photoshop document | `document.importPsd` with `{ "id":"art", "path":"source.psd" }`. Set `flattened` to `true` to import the saved composite. Use base64 `data` in place of `path` to embed the PSD. |
| Import PNG, JPEG, or WebP as a document | `document.importImage` with `{ "id":"photo", "name":"Photo", "path":"input.webp" }` |
| Import an image as a layer | `layer.importImage` with the same arguments and optional `parent` |
| Paste an image file | `layer.pastePixels` with `{ "id":"paste", "name":"Pasted", "path":"input.png", "x":10, "y":20 }` |
| Add a linked Chop Layer | `layer.createChop` with `{ "id":"linked", "name":"Linked", "snapshot":{ "sourcePath":"source.chop" } }` |
| Re-bake or replace a linked Chop Layer | `layer.rebakeChop` with the layer ID as `target` and `{ "sourcePath":"source.chop" }` as its arguments |
| Export PSD | `document.export` with `{ "path":"output.psd", "format":"psd" }`. The default `flattenLayers:true, keepLayersSeparate:true` bakes each layer separately. Set `keepLayersSeparate:false` with flattening enabled for one merged layer. Set `flattenLayers:false` for editable export, which preserves compatible solid fills, basic shapes, masks, and selected styles and rasterizes unsupported features. PSD export uses 8-bit RGB. |
| Export PNG, JPEG, WebP, or SVG | `document.export` with `{ "path":"output.webp", "format":"webP" }`. Supported format values are `png`, `jpeg`, `webP`, and `svg`. Omitting `format` exports PNG. |

The command log records changes made from mouse, keyboard, and menu commands. Clipboard edits record the image or mask payload, so a replay can use those pixels without an operating-system clipboard. For scripts written by hand, inputs can be paths or embedded bytes. Layer commands that read paths capture their inputs so history edits can replay them even if the source file has changed.

## Architecture and format

- `CHOP.Core` owns documents, layer trees, effects, command validation, history, script execution, and native files. It has no Windows or GUI dependency. Every desktop change to document state or generated document output must execute a shared command so it can be recorded and replayed by `chop run`.
- `CHOP.Rendering` implements the raster backend with SkiaSharp. Immutable source buffers and cached effect nodes keep rendered pixels separate from editable content. An effect evaluation references its input node, and the UI evaluates composites on a worker and reuses them during viewport navigation.
- `PhotoChop.csproj` builds the `CHOP.Desktop` WPF application. The desktop and `CHOP.CLI` share the command dispatcher and renderer.
- `CHOP.Tests` covers command validation, editing, rendering, native persistence, and CLI replay.

A `.chop` file is a ZIP container with a version 8 `manifest.json`, embedded `pixels/<layer-id>.rgba`, and optional mask and pixel edit resources. CHOP also opens versions 1 through 7 and merges enabled legacy spatial edits into one transform. The manifest records guides in whole-pixel document coordinates. The manifest specifies sRGB canvas dimensions, document ID and metadata, layer order and hierarchy, content and parameters, and resource dimensions and relative entry paths. Pixel resources use row-major straight-alpha RGBA8. Saving writes a sibling temporary file and replaces the destination after the ZIP is complete.

Phase 1 uses CPU compositing, binary pixel selections, and raster snapshots for undo. History retains up to 250 states with a 384 MiB shared raster budget, plus the latest state. Complex transformed effects must fit the renderer's image size limits. Editable text uses the bundled Liberation Sans by default. Click existing text with the Text tool or double-click its layer to edit the text, font, size, and color. Other fonts depend on what is installed on the rendering machine.
