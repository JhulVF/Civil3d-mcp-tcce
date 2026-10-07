import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainActionDefinition, DomainToolDefinition } from "../domainRuntime.js";
import type { ToolCapability } from "../toolMetadata.js";

/** Style editing (TCCE). Each action forwards its arguments (minus `action`) to the plugin method. */
const ACTIONS: Array<{ action: string; method: string; caps: ToolCapability[]; write: boolean }> = [
  { action: "list_collections", method: "styleListCollections", caps: ["query"], write: false },
  { action: "list_styles", method: "styleList", caps: ["query"], write: false },
  { action: "get_style", method: "styleGet", caps: ["inspect"], write: false },
  { action: "create_style", method: "styleCreate", caps: ["create"], write: true },
  { action: "rename_style", method: "styleRename", caps: ["edit"], write: true },
  { action: "delete_style", method: "styleDelete", caps: ["delete"], write: true },
  { action: "set_properties", method: "styleSetProperties", caps: ["edit"], write: true },
  { action: "set_display", method: "styleSetDisplay", caps: ["edit"], write: true },
  { action: "set_label_component", method: "styleSetLabelComponent", caps: ["edit"], write: true },
  { action: "add_label_component", method: "styleAddLabelComponent", caps: ["create"], write: true },
  { action: "remove_label_component", method: "styleRemoveLabelComponent", caps: ["delete"], write: true },
  { action: "label_set_add", method: "styleLabelSetAdd", caps: ["edit"], write: true },
  { action: "label_set_remove", method: "styleLabelSetRemove", caps: ["edit"], write: true },
  { action: "label_set_edit", method: "styleLabelSetEdit", caps: ["edit"], write: true },
  { action: "import_styles", method: "styleImport", caps: ["import"], write: true },
  { action: "assign_style", method: "styleAssign", caps: ["edit"], write: true },
  { action: "assign_label_set", method: "styleAssignLabelSet", caps: ["edit"], write: true },
];

const Change = z.object({
  path: z.string().describe("Property path, e.g. 'ContourStyle.MajorInterval', 'Text.Height', 'Text.Contents', 'General.Visible', 'Properties.Label.Visibility'."),
  value: z.union([z.number(), z.boolean(), z.string()]),
});

const shape = {
  action: z.enum(ACTIONS.map((a) => a.action) as [string, ...string[]]).describe("Style operation."),
  collection: z
    .string()
    .optional()
    .describe(
      "Style collection path under the drawing's styles (see list_collections), e.g. AlignmentStyles, ProfileStyles, SurfaceStyles, CorridorStyles, " +
        "ProfileViewStyles, LabelStyles.AlignmentLabelStyles.MajorStationLabelStyles, LabelSetStyles.AlignmentLabelSetStyles."
    ),
  filter: z.string().optional().describe("list_collections: text filter on the path (e.g. 'Alignment')."),
  name: z.string().optional().describe("Style name."),
  newName: z.string().optional().describe("rename_style: new name."),
  copyFrom: z.string().optional().describe("create_style: existing style in the same collection to copy."),
  depth: z.number().optional().describe("get_style: nesting depth of property dump (default 2)."),
  changes: z.array(Change).optional().describe("set_properties / set_label_component / add_label_component: property changes."),
  path: z.string().optional().describe("Single property path (alternative to changes)."),
  value: z.union([z.number(), z.boolean(), z.string()]).optional().describe("Value for 'path'."),
  // display
  view: z.enum(["plan", "model", "profile", "section", "all"]).optional().describe("set_display: which view (default plan)."),
  components: z.array(z.string()).optional().describe("set_display: display component names from get_style (e.g. Line, Curve, MajorContour) or ['*']."),
  visible: z.boolean().optional(),
  layer: z.string().optional().describe("Layer (created if missing)."),
  color: z.union([z.number(), z.string()]).optional().describe("ACI number, 'r,g,b', ByLayer, ByBlock or red/yellow/green/cyan/blue/magenta/white/gray."),
  linetype: z.string().optional().describe("Linetype name (must be loaded) or ByLayer/ByBlock."),
  lineweight: z.union([z.number(), z.string()]).optional().describe("mm (0.30), ByLayer, ByBlock or LineWeight030."),
  linetypeScale: z.number().optional(),
  plotStyle: z.string().optional(),
  // label components
  component: z.string().optional().describe("Label style component name (see get_style)."),
  componentType: z.enum(["Text", "Line", "Block", "Tick", "ReferenceText", "DirectionArrow", "TextForEach"]).optional(),
  // label sets
  labelStyle: z.string().optional().describe("label_set_add/edit: label style name."),
  labelStyleCollection: z.string().optional().describe("Collection path of labelStyle when the name is ambiguous."),
  increment: z.number().optional().describe("label_set_add/edit: increment (e.g. 100 for major stations)."),
  index: z.number().optional().describe("label_set_remove/edit: item index from get_style."),
  // import
  filePath: z.string().optional().describe("import_styles: template .dwt/.dwg path."),
  names: z.array(z.string()).optional().describe("import_styles: style names to import (default: all in the collection)."),
  conflict: z.enum(["Override", "Rename", "Ignore"]).optional().describe("import_styles: when a style already exists (default Override)."),
  // assignment
  style: z.string().optional().describe("assign_style: style name to apply."),
  handles: z.array(z.string()).optional().describe("Target object handles."),
  objectType: z.enum(["alignment", "profile", "profileView", "surface", "corridor", "assembly", "sampleLineGroup"]).optional(),
  objectName: z.string().optional().describe("Target object name ('*' = all of that type)."),
  objectNames: z.array(z.string()).optional(),
  labelSet: z.string().optional().describe("assign_label_set: alignment label set name."),
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

export const STYLE_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "style",
  actions,
  exposures: [
    {
      toolName: "civil3d_style",
      displayName: "Civil 3D Styles",
      description:
        "Read and edit Civil 3D styles. Start with list_collections (paths) → list_styles → get_style (properties, display components per view, " +
        "label components, label set items). Edit with set_display (layer/color/linetype/lineweight/visibility of display components, e.g. surface " +
        "MajorContour/MinorContour, alignment Line/Curve), set_properties (any property path, e.g. ContourStyle.MajorInterval), label styles with " +
        "set_label_component / add_label_component / remove_label_component (paths like Text.Height, Text.Color, Text.Contents, General.Visible), " +
        "label sets with label_set_add / label_set_edit / label_set_remove (increments). Manage with create_style (copyFrom), rename_style, " +
        "delete_style, import_styles (from a TCCE template .dwt), assign_style (set StyleId on alignments, profiles, surfaces, corridors, etc.) and " +
        "assign_label_set (alignments). Prefer copying a style (create_style copyFrom) before editing a shared one.",
      inputShape: shape,
      supportedActions: ACTIONS.map((a) => a.action),
      resolveAction: (rawArgs) => ({ action: String(rawArgs.action), args: rawArgs }),
    },
  ],
};
