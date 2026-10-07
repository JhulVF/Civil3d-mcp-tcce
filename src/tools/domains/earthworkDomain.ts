import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainActionDefinition, DomainToolDefinition } from "../domainRuntime.js";
import type { ToolCapability } from "../toolMetadata.js";

/** Earthwork (TCCE). Each action forwards its arguments (minus `action`) to the plugin method. */
const ACTIONS: Array<{ action: string; method: string; caps: ToolCapability[]; write: boolean }> = [
  { action: "corridor_surfaces", method: "ewCorridorSurfaces", caps: ["create"], write: true },
  { action: "corridor_codes", method: "ewCorridorCodes", caps: ["inspect"], write: false },
  { action: "create_feature_line", method: "ewCreateFeatureLine", caps: ["create"], write: true },
  { action: "add_breaklines", method: "ewAddBreaklines", caps: ["edit"], write: true },
  { action: "add_boundary", method: "ewAddBoundary", caps: ["edit"], write: true },
  { action: "create_pad", method: "ewCreatePad", caps: ["create", "analyze"], write: true },
  { action: "compute_volumes", method: "ewComputeVolumes", caps: ["analyze"], write: true },
  { action: "section_volumes", method: "ewSectionVolumes", caps: ["analyze"], write: true },
];

const shape = {
  action: z.enum(ACTIONS.map((a) => a.action) as [string, ...string[]]).describe("Earthwork operation."),
  // corridor surfaces
  corridor: z.string().optional().describe("Corridor name."),
  top: z.boolean().optional().describe("corridor_surfaces: create Top surface (default true)."),
  datum: z.boolean().optional().describe("corridor_surfaces: create Datum surface (default true)."),
  topName: z.string().optional().describe("Default '<corridor> - Top'."),
  datumName: z.string().optional().describe("Default '<corridor> - Datum'."),
  topCode: z.string().optional().describe("Link code for Top (default 'Top')."),
  datumCode: z.string().optional().describe("Link code for Datum (default 'Datum')."),
  topStyle: z.string().optional(),
  datumStyle: z.string().optional(),
  extentsBoundary: z.boolean().optional().describe("Add corridor-extents outer boundary (default true)."),
  replace: z.boolean().optional().describe("Rebuild existing corridor surfaces with the same name."),
  // feature lines / breaklines / boundaries
  polylineHandle: z.string().optional().describe("Polyline handle (feature line source, pad outline, or boundary)."),
  name: z.string().optional().describe("Name of the feature line / pad surface."),
  site: z.string().optional().describe("Site for the feature line (default: no site)."),
  style: z.string().optional().describe("Feature line style / pad surface style."),
  elevation: z.number().optional().describe("Constant elevation (feature line) or pad elevation (create_pad; default = average EG along the edge)."),
  fromSurface: z.string().optional().describe("create_feature_line: take elevations from this surface."),
  includeIntermediate: z.boolean().optional(),
  startElevation: z.number().optional().describe("create_feature_line: start elevation, combined with grade (%)."),
  grade: z.number().optional().describe("create_feature_line: grade in percent along the line."),
  surface: z.string().optional().describe("add_breaklines / add_boundary: target TIN surface."),
  handles: z.array(z.string()).optional().describe("add_breaklines: polyline / 3D polyline / feature line handles."),
  type: z.string().optional().describe("add_breaklines: standard|proximity|nondestructive. add_boundary: Outer|Hide|Show|DataClip."),
  midOrdinate: z.number().optional(),
  supplementDistance: z.number().optional(),
  nonDestructive: z.boolean().optional(),
  points: z.array(z.object({ x: z.number(), y: z.number() })).optional().describe("add_boundary: polygon points instead of a polyline."),
  // pad
  targetSurface: z.string().optional().describe("create_pad: existing ground surface to daylight to (EG)."),
  cutSlope: z.number().optional().describe("H:V horizontal per 1 vertical (default 2 = 2:1)."),
  fillSlope: z.number().optional().describe("H:V (default 3 = 3:1)."),
  spacing: z.number().optional().describe("create_pad: edge densification in drawing units (default 10)."),
  maxDistance: z.number().optional().describe("create_pad: max daylight search distance (default 500)."),
  cornerFanDegrees: z.number().optional().describe("create_pad: angular step of rays at convex corners (default 15)."),
  balance: z.boolean().optional().describe("create_pad: find the pad elevation that balances adjusted cut and fill."),
  computeVolume: z.boolean().optional().describe("create_pad: compute cut/fill vs target surface (default true)."),
  keepVolumeSurface: z.boolean().optional(),
  layer: z.string().optional().describe("Layer for pad edge/daylight 3D polylines (default C-GRAD-PAD)."),
  // volumes
  baseSurface: z.string().optional().describe("Base surface (usually EG)."),
  comparisonSurface: z.string().optional().describe("compute_volumes: comparison surface (Datum, pad, FG)."),
  designSurface: z.string().optional().describe("section_volumes: design surface (corridor Datum / FG)."),
  cutFactor: z.number().optional().describe("Multiplier applied to cut (swell), default 1."),
  fillFactor: z.number().optional().describe("Multiplier applied to fill (compaction), default 1."),
  boundaryHandles: z.array(z.string()).optional().describe("compute_volumes: closed polylines; one result per region plus total."),
  keepAs: z.string().optional().describe("compute_volumes: keep the volume surface(s) with this name."),
  alignment: z.string().optional().describe("section_volumes: alignment for stationing."),
  interval: z.number().optional().describe("section_volumes: station interval (default 25)."),
  leftWidth: z.number().optional().describe("section_volumes: sampling width left (default 100)."),
  rightWidth: z.number().optional().describe("section_volumes: sampling width right (default 100)."),
  sampleStep: z.number().optional().describe("section_volumes: spacing across each section (default 1)."),
  startStation: z.number().optional(),
  endStation: z.number().optional(),
  outputPath: z.string().optional().describe("section_volumes: write the table as CSV on the user's PC."),
  drawMassHaul: z
    .object({ x: z.number(), y: z.number(), horizontalScale: z.number().optional(), verticalScale: z.number().optional(), layer: z.string().optional() })
    .optional()
    .describe("section_volumes: draw the mass haul line (CY) as a polyline at x,y."),
};

const actions: Record<string, DomainActionDefinition> = {};
for (const a of ACTIONS) {
  actions[a.action] = {
    action: a.action,
    inputSchema: z.object({ action: z.literal(a.action) }).passthrough() as any,
    capabilities: a.caps,
    requiresActiveDrawing: true,
    safeForRetry: !a.write,
    pluginMethods: [a.method],
    execute: async (args: any) => {
      const { action: _ignored, ...rest } = args ?? {};
      return await withApplicationConnection(async (c) => await c.sendCommand(a.method, rest));
    },
  };
}

export const EARTHWORK_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "earthwork",
  actions,
  exposures: [
    {
      toolName: "civil3d_earthwork",
      displayName: "Civil 3D Earthwork",
      description:
        "Earthwork in Civil 3D. corridor_surfaces: create the corridor Top (link code Top, top-links overhang) and Datum (Datum, bottom-links) " +
        "surfaces with an extents boundary (corridor_codes lists available codes). create_pad: pad from a closed polyline at an elevation " +
        "(or balance=true), daylighting to EG with cut/fill slopes H:V; builds a TIN surface and returns cut/fill. compute_volumes: cut/fill/net " +
        "between two surfaces with cut/fill factors, optionally per boundary polygon (e.g. per pad or phase). section_volumes: average-end-area " +
        "table by station between EG and a design surface (e.g. corridor Datum), mass haul ordinates, CSV export and an optional mass haul polyline. " +
        "Also create_feature_line (from polyline, constant/grade/surface elevations), add_breaklines and add_boundary on TIN surfaces. " +
        "Note: Civil 3D's .NET API cannot create Grading objects; pads are computed geometrically. Volumes in drawing units³ (CY assumes feet).",
      inputShape: shape,
      supportedActions: ACTIONS.map((a) => a.action),
      resolveAction: (rawArgs) => ({ action: String(rawArgs.action), args: rawArgs }),
    },
  ],
};
