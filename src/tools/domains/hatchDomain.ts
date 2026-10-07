import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

const shape = {
  action: z.enum(["get"]).describe("The hatch operation to perform."),
  handles: z.array(z.string()).optional().describe("Read these hatches by handle (takes priority over the selection)."),
  prompt: z
    .boolean()
    .optional()
    .describe("If nothing is pre-selected, ask the user to pick hatches in Civil 3D (the command line waits for them)."),
  all: z.boolean().optional().describe("If nothing is selected, read every hatch in model space."),
  layer: z.string().optional().describe("Only hatches on this layer."),
  includeLoops: z.boolean().optional().describe("Include boundary loop geometry (default true)."),
  maxVertices: z.number().optional().describe("Max vertices/segments per loop (default 500)."),
  limit: z.number().optional().describe("Max hatches returned (default 200)."),
};

export const HATCH_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "hatch",
  actions: {
    get: {
      action: "get",
      inputSchema: z.object({
        action: z.literal("get"),
        handles: z.array(z.string()).optional(),
        prompt: z.boolean().optional(),
        all: z.boolean().optional(),
        layer: z.string().optional(),
        includeLoops: z.boolean().optional(),
        maxVertices: z.number().optional(),
        limit: z.number().optional(),
      }),
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getHatches"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("getHatches", {
            handles: args.handles,
            prompt: args.prompt,
            all: args.all,
            layer: args.layer,
            includeLoops: args.includeLoops,
            maxVertices: args.maxVertices,
            limit: args.limit,
          })
        ),
    },
  },
  exposures: [
    {
      toolName: "civil3d_hatch",
      displayName: "Civil 3D Hatches",
      description:
        "Read hatch objects in Civil 3D. Action 'get' reads the hatches the user has selected (pickfirst); " +
        "if none are selected, use prompt=true to ask the user to pick them, all=true for every hatch in model space, " +
        "or pass handles. Returns per hatch: handle, layer, color, pattern name/type/scale/angle, solid/gradient, " +
        "associativity and boundary handles, area (drawing units²), extents and boundary loops (polyline vertices with " +
        "bulge, or line/arc segments), plus the total area.",
      inputShape: shape,
      supportedActions: ["get"],
      resolveAction: (rawArgs) => ({ action: String(rawArgs.action), args: rawArgs }),
    },
  ],
};
