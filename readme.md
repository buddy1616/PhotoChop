# CHOP

CHOP is a Windows desktop editor for layered RGBA images with editable operations and a scriptable command engine.

## Build and run

Install the .NET 10 SDK. On Windows, open `CHOP.slnx` or run these commands from the repository root.

```sh
dotnet build CHOP.slnx
dotnet test CHOP.slnx
dotnet run --project PhotoChop.csproj
```

The solution also cross-builds on Linux. Run the WPF application on Windows with the .NET 10 Desktop Runtime. The CLI and automated tests run headlessly on Windows and Linux. On Linux, Skia uses Fontconfig for locally installed fonts.

## Desktop workflow

Create a document with **File → New**. The default is 1920 × 1080 with a transparent background. A document can start with a solid color entered as `#AARRGGBB`. **Image → Default document size** saves a new default. Import a PNG with **File → Import PNG**, or use **Layer → New raster layer** to paint on an empty layer.

The Layers panel lists the topmost layer first. Drag to reorder, hold Shift while dropping on a group to put the layer inside it, and use Ctrl or Shift to select multiple layers for grouping and merging. Properties edits the selected layer's visibility, lock, opacity, blend, transform, text, shape fill, and effects. Merge and flatten bake a composite onto a raster layer after confirmation. Merging requires adjacent sibling layers and can change blends that depend on the backdrop outside that selection. Ungroup requires a visible group with default transform, normal blending, full opacity, and an empty effects stack.

Use the tools on the left and adjust brush or shape options above the canvas. Colors accept `#AARRGGBB`. A brush or move gesture records a single completed command. Crop changes the canvas bounds, leaving source pixels in the editable layers. Effect rows run top to bottom. The last enabled blend override determines how the result blends with layers underneath. Transform effects run in order within the stack, before the layer's native transform for raster, text, and shape layers. Groups composite their children as an isolated surface before group effects.

Drag a panel header to a docking target or outside the main window to float it. Drag tabs to rearrange them. Use the pin control to auto-hide a docked panel and **Window** to show hidden panels or reset the docking layout. Layout and preferences are stored under the current user's local application data in `CHOP`.

Open [the editable sample](examples/phase-one.chop) from the File menu to try text, shapes, paint, groups, transparency, and a hue effect.

| Shortcut | Action |
| --- | --- |
| Ctrl+N / Ctrl+O | New / Open |
| Ctrl+S / Ctrl+Shift+S / Ctrl+Shift+E | Save / Save As / Export PNG |
| Ctrl+W | Close document |
| Ctrl+Z / Ctrl+Y | Undo / Redo |
| Ctrl+D | Deselect |
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

A `.chop` file is a ZIP container with a version 1 `manifest.json` and embedded `pixels/<layer-id>.rgba` resources. The manifest specifies sRGB canvas dimensions, document ID and metadata, layer order and hierarchy, content and parameters, and resource dimensions and relative entry paths. Pixel resources use row-major straight-alpha RGBA8. Saving writes a sibling temporary file and replaces the destination after the ZIP is complete.

Phase 1 uses CPU compositing, rectangular selections, and raster snapshots for paint undo. History retains up to 250 states with a 384 MiB shared raster budget, plus the latest state. Complex transformed effects must fit the renderer's image size limits. Editable text uses the bundled Liberation Sans by default. Other fonts depend on what is installed on the rendering machine. Layer masks are reserved for a later format revision.
