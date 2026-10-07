import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainActionDefinition, DomainToolDefinition } from "../domainRuntime.js";
import type { ToolCapability } from "../toolMetadata.js";

/**
 * Road modeling workflow (TCCE). One MCP tool, many actions; each action forwards its
 * arguments (minus `action`) to the plugin method of the same row.
 */
const ACTIONS: Array<{ action: string; method: string; caps: ToolCapability[]; write: boolean }> = [
  { action: "list_styles", method: "roadListStyles", caps: ["query"], write: false },
  { action: "create_alignment_from_polyline", method: "roadCreateAlignmentFromPolyline", caps: ["create"], write: true },
  { action: "create_alignment_by_pis", method: "roadCreateAlignmentByPIs", caps: ["create"], write: true },
  { action: "create_offset_alignment", method: "roadCreateOffsetAlignment", caps: ["create"], write: true },
  { action: "set_alignment_design", method: "roadSetAlignmentDesign", caps: ["edit"], write: true },
  { action: "get_alignment_geometry", method: "roadGetAlignmentGeometry", caps: ["inspect"], write: false },
  { action: "create_surface_profile", method: "roadCreateSurfaceProfile", caps: ["create"], write: true },
  { action: "create_layout_profile", method: "roadCreateLayoutProfile", caps: ["create"], write: true },
  { action: "get_profile_geometry", method: "roadGetProfileGeometry", caps: ["inspect"], write: false },
  { action: "create_profile_view", method: "roadCreateProfileView", caps: ["create"], write: true },
  { action: "create_assembly", method: "roadCreateAssembly", caps: ["create"], write: true },
  { action: "get_assembly", method: "roadGetAssembly", caps: ["inspect"], write: false },
  { action: "set_subassembly_params", method: "roadSetSubassemblyParams", caps: ["edit"], write: true },
  { action: "create_corridor", method: "roadCreateCorridor", caps: ["create"], write: true },
  { action: "set_corridor_targets", method: "roadSetCorridorTargets", caps: ["edit"], write: true },
  { action: "add_corridor_surface", method: "roadAddCorridorSurface", caps: ["create"], write: true },
  { action: "get_corridor_info", method: "roadGetCorridorInfo", caps: ["inspect"], write: false },
  { action: "create_sample_lines", method: "roadCreateSampleLines", caps: ["create"], write: true },
  { action: "create_section_views", method: "roadCreateSectionViews", caps: ["create"], write: true },
  { action: "apply_superelevation", method: "roadApplySuperelevation", caps: ["edit"], write: true },
  { action: "check_design", method: "roadCheckDesign", caps: ["analyze"], write: false },
];

const XY = z.object({ x: z.number(), y: z.number() });

const shape = {
  action: z.enum(ACTIONS.map((a) => a.action) as [string, ...string[]]).describe("Road modeling operation."),
  // common names
  name: z.string().optional().describe("Name of the object to create (alignment, profile, assembly, corridor, corridor surface, view)."),
  alignment: z.string().optional().describe("Alignment name."),
  profile: z.string().optional().describe("Profile name."),
  surface: z.string().optional().describe("Surface name (create_surface_profile)."),
  assembly: z.string().optional().describe("Assembly name."),
  corridor: z.string().optional().describe("Corridor name."),
  style: z.string().optional().describe("Style name (see list_styles). Default: first style in the drawing."),
  labelSet: z.string().optional().describe("Label set name (alignments/profiles)."),
  layer: z.string().optional().describe("Layer (created if missing)."),
  // alignment
  polylineHandle: z.string().optional().describe("create_alignment_from_polyline: polyline handle (use civil3d_block list_polylines)."),
  addCurvesBetweenTangents: z.boolean().optional().describe("create_alignment_from_polyline: add curves at polyline vertices (uses drawing default radius)."),
  eraseExisting: z.boolean().optional().describe("create_alignment_from_polyline: erase the source polyline."),
  pis: z
    .array(z.object({ x: z.number(), y: z.number(), radius: z.number().optional(), spiralIn: z.number().optional(), spiralOut: z.number().optional() }))
    .optional()
    .describe("create_alignment_by_pis: start point, PIs, end point. Interior PIs get a circular curve of 'radius' (or SCS if spiral lengths given)."),
  defaultRadius: z.number().optional().describe("create_alignment_by_pis: radius for PIs without one."),
  startStation: z.number().optional().describe("Start station (alignment reference station, or region/sample-line start)."),
  endStation: z.number().optional().describe("End station (region / sample lines)."),
  designSpeed: z.number().optional().describe("Design speed in drawing speed units (e.g. 30 mph)."),
  useDesignSpeed: z.boolean().optional(),
  criteriaFile: z.string().optional().describe("Design criteria XML file (e.g. _Autodesk Civil 3D Imperial (2011) Roadway Design Standards.xml)."),
  offset: z.number().optional().describe("create_offset_alignment: offset (+ right / - left). create_surface_profile: sampling offset."),
  // profile
  pvis: z
    .array(z.object({ station: z.number(), elevation: z.number(), curveLength: z.number().optional(), k: z.number().optional() }))
    .optional()
    .describe("create_layout_profile: PVIs from start to end. Interior PVIs get a symmetric parabola by k or curveLength."),
  x: z.number().optional().describe("Insertion X (profile view, section views, assembly)."),
  y: z.number().optional().describe("Insertion Y."),
  bandSet: z.string().optional().describe("create_profile_view: band set name."),
  // assembly
  type: z.string().optional().describe("create_assembly: UndividedCrownedRoad (default), UndividedPlanarRoad, DividedCrownedRoad, DividedPlanarRoad, Other."),
  codeSetStyle: z.string().optional(),
  subassemblies: z
    .array(
      z.object({
        stock: z.string().describe("Stock subassembly class, e.g. LaneSuperelevationAOR, ShoulderExtendSubbase, BasicLane, BasicShoulder, DaylightGeneral, BasicSideSlopeCutDitch, LinkSlopeToSurface."),
        side: z.enum(["Left", "Right"]).optional(),
        name: z.string().optional(),
        params: z.record(z.union([z.number(), z.boolean(), z.string()])).optional().describe("Parameter key or display name → value (see get_assembly)."),
        attach: z.enum(["previous", "assembly"]).optional().describe("Hook to the outer point of the previous subassembly on the same side (default) or to the assembly baseline."),
      })
    )
    .optional()
    .describe("create_assembly: subassemblies in order, inside to outside, per side."),
  includeParams: z.boolean().optional(),
  subassembly: z.string().optional().describe("set_subassembly_params: subassembly name."),
  params: z.record(z.union([z.number(), z.boolean(), z.string()])).optional().describe("set_subassembly_params: key/display name → value."),
  // corridor
  baselineName: z.string().optional(),
  regionName: z.string().optional(),
  targetSurface: z.string().optional().describe("Surface used by daylight/slope subassembly targets (EG)."),
  frequencyTangents: z.number().optional().describe("Assembly insertion frequency on tangents (default 25)."),
  frequencyCurves: z.number().optional().describe("On curves (default 10)."),
  frequencySpirals: z.number().optional(),
  frequencyProfileCurves: z.number().optional(),
  linkCodes: z.array(z.string()).optional().describe("add_corridor_surface: link codes, e.g. ['Top'] or ['Datum'] (default ['Top'])."),
  featureLineCodes: z.array(z.string()).optional(),
  extentsBoundary: z.boolean().optional().describe("add_corridor_surface: add corridor-extents outer boundary (default true)."),
  // sample lines / sections
  groupName: z.string().optional().describe("Sample line group name."),
  interval: z.number().optional().describe("create_sample_lines: station interval (default 50)."),
  leftWidth: z.number().optional().describe("Swath width left (default 50)."),
  rightWidth: z.number().optional().describe("Swath width right (default 50)."),
  includeGeometryPoints: z.boolean().optional().describe("Also sample at horizontal geometry points (default true)."),
  // superelevation
  e: z.number().optional().describe("apply_superelevation: full superelevation as decimal (default 0.04)."),
  normalCrown: z.number().optional().describe("Normal crown slope as decimal (default 0.02)."),
  laneWidth: z.number().optional().describe("Rotated lane width (default 11)."),
  relativeGradient: z.number().optional().describe("Max relative gradient as decimal (default 0.0066 for 30 mph)."),
  runoffLength: z.number().optional().describe("Override superelevation runoff length."),
  runoffOnTangent: z.number().optional().describe("Fraction of runoff on tangent (default 0.667)."),
  // design check
  minRadius: z.number().optional(),
  maxGrade: z.number().optional().describe("Percent."),
  minGrade: z.number().optional().describe("Percent."),
  kCrest: z.number().optional(),
  kSag: z.number().optional(),
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

export const ROAD_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "road",
  actions,
  exposures: [
    {
      toolName: "civil3d_road",
      displayName: "Civil 3D Road Modeling",
      description:
        "Full road-modeling workflow in Civil 3D (use this instead of the 'planned' create actions of civil3d_alignment/civil3d_profile). " +
        "Typical order: list_styles → create_alignment_by_pis (PIs + radii) or create_alignment_from_polyline → set_alignment_design (speed, criteria) → " +
        "create_surface_profile (EG) → create_profile_view → create_layout_profile (PVIs with K or curve length) → check_design (Rmin, grades, K) → " +
        "apply_superelevation (experimental: AASHTO critical stations) → create_assembly (stock subassemblies, e.g. LaneSuperelevationAOR 11 ft + ShoulderExtendSubbase/BasicShoulder + DaylightGeneral) → " +
        "get_assembly (see parameter keys) / set_subassembly_params → create_corridor (alignment+profile+assembly, frequencies, target EG) → " +
        "add_corridor_surface (Top / Datum) → create_sample_lines → create_section_views. Also: get_alignment_geometry, get_profile_geometry, " +
        "create_offset_alignment, set_corridor_targets, get_corridor_info. Earthwork: civil3d_surface compute_volume between EG and the Datum surface.",
      inputShape: shape,
      supportedActions: ACTIONS.map((a) => a.action),
      resolveAction: (rawArgs) => ({ action: String(rawArgs.action), args: rawArgs }),
    },
  ],
};
