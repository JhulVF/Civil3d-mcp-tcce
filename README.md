# Civil 3D MCP Server — TCCE edition

An MCP (Model Context Protocol) server that lets Claude work inside **Autodesk Civil 3D 2027** through natural language: read and draw geometry, model roads, compute earthwork and manage styles.

This is TCCE's fork of [barbosaihan/civil3d-mcp](https://github.com/barbosaihan/civil3d-mcp). It adds:

- Support for Civil 3D 2027 (.NET 10).
- Fixes so the project compiles.
- Six new tool groups: blocks, hatches, road design, styles, earthwork, and surface sampling.

> **Status:** the plugin compiles against the Civil 3D 2027 API. Tools marked *(to verify)* below have not yet been fully tested on a real drawing. Try them on a test drawing first.

## Architecture

```
┌──────────────┐   stdio   ┌───────────────────┐   TCP / JSON-RPC   ┌────────────────────┐
│    Claude    │ ◄───────► │  MCP Server (TS)  │ ◄────────────────► │  Civil 3D Plugin   │
│  (desktop)   │           │  Node.js          │     port 8080      │  (.NET 10, C#)     │
└──────────────┘           └───────────────────┘                    └─────────┬──────────┘
                                                                              │
                                                                       Civil 3D .NET API
```

1. **MCP Server** (TypeScript/Node.js): talks to Claude over the MCP protocol.
2. **Civil 3D Plugin** (C#): runs inside Civil 3D and calls the Civil 3D API.

## Tools

### Original tools

| Tool | Actions |
|---|---|
| `civil3d_health` | health |
| `civil3d_drawing` | info, settings, save, undo, redo, list_object_types, get_selected |
| `civil3d_surface` | list, get, get_elevation, get_statistics, create, delete, add_points, compute_volume, **sample_grid**, **get_elevations** |
| `civil3d_alignment` | list, get, delete, station_to_point, point_to_station |
| `civil3d_profile` | list, get, get_elevation, delete |
| `civil3d_corridor` | list, get, rebuild |
| `civil3d_pipe` | list_networks, get_network, get_pipe, get_structure, create_network, add_pipe, add_structure, check_interference |
| `civil3d_point` | list, get, create, delete, list_groups, import |
| `civil3d_geometry` | create_line, create_polyline, create_3d_polyline, create_text, create_mtext |

Some original actions still return `planned`:

- In `civil3d_alignment` and `civil3d_profile`: create. Use `civil3d_road` instead.
- In `civil3d_surface`: add_breakline, add_boundary, extract_contours. Use `civil3d_earthwork` for the first two.
- In `civil3d_corridor`: corridor surfaces and volumes. Use `civil3d_earthwork` instead.

### TCCE tools

| Tool | What it does |
|---|---|
| **`civil3d_block`** | `list_polylines` (closed polylines with area and vertices), `list_blocks` (definitions with extents), `insert_blocks` (multiple points, rotation, scale, layer, default attributes), `create_block` (from geometry specs — polyline, line, circle, arc, text, attribute — or from existing objects by handle), `import_block` (from another DWG) |
| **`civil3d_hatch`** | `get`: pattern, scale, angle, area, layer, color, associativity and boundary loops of selected hatches (or `prompt`, `all`, `handles`) |
| **`civil3d_road`** | Full road workflow: `list_styles`, `create_alignment_by_pis` (PIs + radii, optional spirals), `create_alignment_from_polyline`, `create_offset_alignment`, `set_alignment_design` (design speed, criteria file), `get_alignment_geometry`, `create_surface_profile`, `create_layout_profile` (PVIs with K or curve length), `get_profile_geometry`, `create_profile_view`, `create_assembly` (stock subassemblies), `get_assembly`, `set_subassembly_params`, `create_corridor` (frequencies, EG target), `set_corridor_targets`, `add_corridor_surface`, `get_corridor_info`, `create_sample_lines`, `create_section_views`, `check_design` (Rmin, grades, K), `apply_superelevation` *(experimental)* |
| **`civil3d_style`** | Read and edit styles: `list_collections`, `list_styles`, `get_style`, `create_style` (copy), `rename_style`, `delete_style`, `set_properties` (any property path, e.g. `ContourStyle.MajorInterval`), `set_display` (layer/color/linetype/lineweight/visibility per component and view), label styles (`set_label_component`, `add_label_component`, `remove_label_component`), label sets (`label_set_add`, `label_set_edit`, `label_set_remove`), `import_styles` (from a template .dwt), `assign_style`, `assign_label_set` *(to verify)* |
| **`civil3d_earthwork`** | `corridor_surfaces` (Top + Datum with extents boundary), `corridor_codes`, `create_pad` (daylight to EG with cut/fill slopes, optional `balance`), `compute_volumes` (cut/fill factors, per boundary polygon), `section_volumes` (average end area by station, mass haul, CSV, optional mass haul polyline), `create_feature_line`, `add_breaklines`, `add_boundary` *(to verify)* |

#### Notes

- **Pads:** the Civil 3D .NET API cannot create Grading objects. `create_pad` computes the daylight line geometrically by projecting the slope from each edge point, with ray fans at convex corners. It draws the pad edge and the daylight as 3D polylines (layer `C-GRAD-PAD`) and builds a TIN surface from them.
- **Volumes:** results are in drawing units³. The CY values assume feet. `cutFactor` multiplies cut (swell) and `fillFactor` multiplies fill (compaction).
- **Styles:** collections are addressed by path, e.g. `SurfaceStyles` or `LabelStyles.AlignmentLabelStyles.MajorStationLabelStyles`. Properties are addressed by path too, e.g. `Text.Height` or `General.Visible`. Copy a style (`create_style` with `copyFrom`) before editing a shared one.

### Suggested road workflow

```
list_styles → create_alignment_by_pis → set_alignment_design → create_surface_profile (EG)
→ create_profile_view → create_layout_profile → check_design → apply_superelevation
→ create_assembly → create_corridor (target EG) → earthwork corridor_surfaces (Top/Datum)
→ create_sample_lines → create_section_views → earthwork section_volumes (EG vs Datum)
```

## Requirements

| Software | Version |
|---|---|
| Civil 3D | **2027** (.NET 10). For 2025/2026 see the note under Build step 2 |
| Node.js | LTS |
| Git | any |
| .NET SDK | **10.x** (`winget install Microsoft.DotNet.SDK.10`) |
| Claude Desktop | current |

## Setup

### 1. Build the MCP server

```powershell
git clone https://github.com/JhulVF/Civil3d-mcp-tcce.git
cd Civil3d-mcp-tcce
npm install
npm run build
```

### 2. Build the Civil 3D plugin

Copy the 5 Autodesk DLLs into `C_References\`. They are proprietary and are **not** included in the repository.

```powershell
$repo = "$PWD"
$src  = "C:\Program Files\Autodesk\AutoCAD 2027"
"accoremgd","AcDbMgd","acmgd","AecBaseMgd","AeccDbMgd" | % {
  $f = Get-ChildItem $src -Recurse -Filter "$_.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
  Copy-Item $f.FullName "$repo\C_References\"; "OK  $($f.Name)"
}
cd plugin\Civil3dMcpPlugin
dotnet build
```

The plugin is written to `plugin\Civil3dMcpPlugin\bin\Debug\net10.0-windows\Civil3dMcpPlugin.dll`.

> **Civil 3D 2025/2026:** these versions use .NET 8. In `Civil3dMcpPlugin.csproj`, change `net10.0-windows` to `net8.0-windows` and install the .NET 8 SDK.

### 3. Load the plugin in Civil 3D

1. Open Civil 3D with any drawing.
2. Type `NETLOAD` and select the DLL. Choose **Always Load** if prompted.
3. Run `C3DMCPSTATUS`. It should report `listener running: True`.

To load the plugin automatically, run `APPLOAD` → **Startup Suite** → add the DLL.

### 4. Configure Claude Desktop

Add the server to `%APPDATA%\Claude\claude_desktop_config.json` inside `"mcpServers"`:

```json
"civil3d": {
  "command": "node",
  "args": ["C:\\path\\to\\Civil3d-mcp-tcce\\build\\index.js"]
}
```

Fully quit Claude (tray icon → **Quit**), reopen it and start a **new** task.

### Updating

After pulling changes, rebuild both parts. Close Civil 3D first, because it locks the DLL.

```powershell
git pull
npm run build
cd plugin\Civil3dMcpPlugin; dotnet build
```

Then reload the plugin with `NETLOAD`, quit and reopen Claude, and start a new task.

## Environment variables

| Variable | Default | Description |
|---|---|---|
| `CIVIL3D_HOST` | `localhost` | Plugin host |
| `CIVIL3D_PORT` | `8080` | Plugin port |
| `CIVIL3D_CONNECT_TIMEOUT` | `5000` | Connection timeout (ms) |
| `CIVIL3D_COMMAND_TIMEOUT` | `120000` | Command timeout (ms) |
| `LOG_LEVEL` | `info` | debug, info, warn, error |

The plugin port is hard-coded to **8080** in `PluginRuntime.cs`. If another add-in (e.g. a Revit MCP) uses 8080, change `Port` there and set `CIVIL3D_PORT` to the same value in the Claude config.

## Plugin commands

| Command | Description |
|---|---|
| `C3DMCPSTART` | Start the TCP listener |
| `C3DMCPSTOP` | Stop the TCP listener |
| `C3DMCPSTATUS` | Check listener status |

## Changes from upstream

- The plugin targets .NET 10 for Civil 3D 2027.
- Added the missing `using Autodesk.Civil.ApplicationServices;` to the Pipe, Surface, Alignment and Corridor command files.
- Replaced APIs that don't exist:
  - `GetCorridorIds()` → `CorridorCollection`.
  - `ActiveProduct?.Name` → `ActiveProduct.ToString()`.
  - `TinSurface.GetVolumeProperties` → a temporary `TinVolumeSurface`.
- Added the TCCE tools listed above.

## License

MIT. See [LICENSE](LICENSE). Original work by barbosaihan; TCCE additions © TCCE.
