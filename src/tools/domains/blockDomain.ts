import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

const BlockActionSchema = z.enum(["list_polylines", "list_blocks", "insert_blocks", "create_block", "import_block"]);

const XY = z.object({ x: z.number(), y: z.number() });

const EntitySpecSchema = z.object({
  type: z.enum(["polyline", "line", "circle", "arc", "text", "attribute"]),
  points: z
    .array(z.object({ x: z.number(), y: z.number(), bulge: z.number().optional() }))
    .optional()
    .describe("polyline (2+ points, optional bulge per vertex) or line (exactly 2)."),
  closed: z.boolean().optional().describe("polyline: closed."),
  center: XY.optional().describe("circle/arc center."),
  radius: z.number().optional().describe("circle/arc radius."),
  startAngle: z.number().optional().describe("arc start angle, degrees."),
  endAngle: z.number().optional().describe("arc end angle, degrees."),
  position: XY.optional().describe("text/attribute insertion point."),
  text: z.string().optional().describe("text content, or attribute default value."),
  tag: z.string().optional().describe("attribute tag."),
  prompt: z.string().optional().describe("attribute prompt."),
  height: z.number().optional().describe("text/attribute height."),
  rotation: z.number().optional().describe("text/attribute rotation, degrees."),
  layer: z.string().optional().describe("Layer inside the block (default '0'; created if missing)."),
  color: z.number().optional().describe("ACI color index (0 = ByBlock, 256 = ByLayer)."),
});

const PointSchema = z.object({
  x: z.number(),
  y: z.number(),
  z: z.number().optional(),
  rotation: z.number().optional().describe("Rotation in degrees for this insertion (overrides the default)."),
});

const canonicalInputShape = {
  action: BlockActionSchema.describe("The block/polyline operation to perform."),
  layer: z.string().optional().describe("list_polylines: filter by layer. insert_blocks: layer for the new references (created if missing)."),
  closedOnly: z.boolean().optional().describe("list_polylines: only closed polylines (default true)."),
  maxVertices: z.number().optional().describe("list_polylines: max vertices returned per polyline (default 500)."),
  limit: z.number().optional().describe("list_polylines: max polylines returned (default 200)."),
  name: z.string().optional().describe("list_blocks: name filter (contains). insert_blocks: exact block name."),
  points: z.array(PointSchema).optional().describe("insert_blocks: insertion points."),
  scale: z.number().optional().describe("insert_blocks: uniform scale (default 1)."),
  rotation: z.number().optional().describe("insert_blocks: default rotation in degrees (default 0)."),
  entities: z.array(EntitySpecSchema).optional().describe("create_block: geometry in BLOCK coordinates (base point = 0,0)."),
  fromHandles: z.array(z.string()).optional().describe("create_block: handles of existing model-space objects to copy into the block (world coordinates)."),
  basePoint: XY.optional().describe("create_block: world point that becomes the block origin when using fromHandles (default 0,0)."),
  eraseOriginals: z.boolean().optional().describe("create_block: erase the source objects after copying (like BLOCK command)."),
  replaceWithReference: z.boolean().optional().describe("create_block: insert a reference of the new block at basePoint."),
  redefine: z.boolean().optional().describe("create_block/import_block: overwrite an existing block definition (existing references update)."),
  filePath: z.string().optional().describe("import_block: full path of the source .dwg."),
  asName: z.string().optional().describe("import_block: block name when importing a whole drawing (default: file name)."),
};

export const BLOCK_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "block",
  actions: {
    list_polylines: {
      action: "list_polylines",
      inputSchema: z.object({
        action: z.literal("list_polylines"),
        layer: z.string().optional(),
        closedOnly: z.boolean().optional(),
        maxVertices: z.number().optional(),
        limit: z.number().optional(),
      }),
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listPolylines"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("listPolylines", {
            layer: args.layer,
            closedOnly: args.closedOnly,
            maxVertices: args.maxVertices,
            limit: args.limit,
          })
        ),
    },
    list_blocks: {
      action: "list_blocks",
      inputSchema: z.object({
        action: z.literal("list_blocks"),
        name: z.string().optional(),
      }),
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listBlocks"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("listBlocks", { name: args.name })
        ),
    },
    insert_blocks: {
      action: "insert_blocks",
      inputSchema: z.object({
        action: z.literal("insert_blocks"),
        name: z.string(),
        points: z.array(PointSchema).min(1),
        scale: z.number().optional(),
        rotation: z.number().optional(),
        layer: z.string().optional(),
      }),
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["insertBlocks"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("insertBlocks", {
            name: args.name,
            points: args.points,
            scale: args.scale,
            rotation: args.rotation,
            layer: args.layer,
          })
        ),
    },
    create_block: {
      action: "create_block",
      inputSchema: z.object({
        action: z.literal("create_block"),
        name: z.string(),
        entities: z.array(EntitySpecSchema).optional(),
        fromHandles: z.array(z.string()).optional(),
        basePoint: XY.optional(),
        eraseOriginals: z.boolean().optional(),
        replaceWithReference: z.boolean().optional(),
        redefine: z.boolean().optional(),
      }),
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createBlock"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("createBlock", {
            name: args.name,
            entities: args.entities,
            fromHandles: args.fromHandles,
            basePoint: args.basePoint,
            eraseOriginals: args.eraseOriginals,
            replaceWithReference: args.replaceWithReference,
            redefine: args.redefine,
          })
        ),
    },
    import_block: {
      action: "import_block",
      inputSchema: z.object({
        action: z.literal("import_block"),
        filePath: z.string(),
        name: z.string().optional(),
        asName: z.string().optional(),
        redefine: z.boolean().optional(),
      }),
      capabilities: ["import"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["importBlock"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("importBlock", {
            filePath: args.filePath,
            name: args.name,
            asName: args.asName,
            redefine: args.redefine,
          })
        ),
    },
  },
  exposures: [
    {
      toolName: "civil3d_block",
      displayName: "Civil 3D Blocks & Polylines",
      description:
        "Read polylines and place blocks in Civil 3D model space. Actions: list_polylines (closed polylines " +
        "with handle, layer, area and vertices — use to find a boundary polygon), list_blocks (block definitions " +
        "with extents relative to the base point), insert_blocks (insert a block at one or more points, with " +
        "optional rotation, scale and layer; attributes are filled with their defaults), create_block (new block " +
        "definition from entity specs in block coordinates — polyline, line, circle, arc, text, attribute — and/or " +
        "from existing objects by handle around a world basePoint), import_block (copy a block definition from " +
        "another .dwg, or the whole drawing as a block).",
      inputShape: canonicalInputShape,
      supportedActions: ["list_polylines", "list_blocks", "insert_blocks", "create_block", "import_block"],
      resolveAction: (rawArgs) => ({
        action: String(rawArgs.action),
        args: rawArgs,
      }),
    },
  ],
};
