import { z } from "zod";
import { withApplicationConnection } from "../../utils/ConnectionManager.js";
import type { DomainToolDefinition } from "../domainRuntime.js";

const Point2DSchema = z.object({ x: z.number(), y: z.number() });
const Point3DSchema = z.object({ x: z.number(), y: z.number(), z: z.number() });

const SurfaceActionSchema = z.enum([
  "list",
  "get",
  "get_elevation",
  "get_statistics",
  "create",
  "delete",
  "add_points",
  "add_breakline",
  "add_boundary",
  "extract_contours",
  "compute_volume",
  "sample_grid",
  "get_elevations",
]);

const canonicalInputShape = {
  action: SurfaceActionSchema.describe("The surface operation to perform."),
  name: z.string().optional().describe("Surface name."),
  x: z.number().optional().describe("X coordinate (for get_elevation)."),
  y: z.number().optional().describe("Y coordinate (for get_elevation)."),
  points: z.array(Point3DSchema).optional().describe("Array of 3D points."),
  style: z.string().optional().describe("Surface style name."),
  layer: z.string().optional().describe("Layer name."),
  description: z.string().optional().describe("Description text."),
  breaklineType: z.enum(["standard", "wall", "proximity"]).optional(),
  boundaryType: z.enum(["show", "hide", "outer", "data_clip"]).optional(),
  boundaryPoints: z.array(Point2DSchema).optional().describe("Boundary polygon points."),
  minorInterval: z.number().optional().describe("Minor contour interval."),
  majorInterval: z.number().optional().describe("Major contour interval."),
  baseSurface: z.string().optional().describe("Base surface for volume calculation."),
  comparisonSurface: z.string().optional().describe("Comparison surface for volume calculation."),
  xyPoints: z.array(Point2DSchema).optional().describe("get_elevations: XY points (max 20,000)."),
  minX: z.number().optional().describe("sample_grid: grid min X."),
  minY: z.number().optional().describe("sample_grid: grid min Y."),
  maxX: z.number().optional().describe("sample_grid: grid max X."),
  maxY: z.number().optional().describe("sample_grid: grid max Y."),
  spacing: z.number().optional().describe("sample_grid: grid spacing (drawing units)."),
  outputPath: z.string().optional().describe("sample_grid: write CSV (x,y,z) to this path on the Civil 3D machine instead of returning the grid inline (required above 40,000 cells)."),
};

export const SURFACE_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "surface",
  actions: {
    list: {
      action: "list",
      inputSchema: z.object({ action: z.literal("list") }),
      capabilities: ["query"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["listSurfaces"],
      execute: async () =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("listSurfaces", {})
        ),
    },
    get: {
      action: "get",
      inputSchema: z.object({ action: z.literal("get"), name: z.string() }),
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSurface"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("getSurface", { name: args.name })
        ),
    },
    get_elevation: {
      action: "get_elevation",
      inputSchema: z.object({
        action: z.literal("get_elevation"),
        name: z.string(),
        x: z.number(),
        y: z.number(),
      }),
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSurfaceElevation"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("getSurfaceElevation", {
            name: args.name,
            x: args.x,
            y: args.y,
          })
        ),
    },
    get_statistics: {
      action: "get_statistics",
      inputSchema: z.object({
        action: z.literal("get_statistics"),
        name: z.string(),
      }),
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSurfaceStatistics"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("getSurfaceStatistics", { name: args.name })
        ),
    },
    create: {
      action: "create",
      inputSchema: z.object({
        action: z.literal("create"),
        name: z.string(),
        style: z.string().optional(),
        layer: z.string().optional(),
        description: z.string().optional(),
      }),
      capabilities: ["create"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["createSurface"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("createSurface", {
            name: args.name,
            style: args.style,
            layer: args.layer,
            description: args.description,
          })
        ),
    },
    delete: {
      action: "delete",
      inputSchema: z.object({
        action: z.literal("delete"),
        name: z.string(),
      }),
      capabilities: ["delete"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["deleteSurface"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("deleteSurface", { name: args.name })
        ),
    },
    add_points: {
      action: "add_points",
      inputSchema: z.object({
        action: z.literal("add_points"),
        name: z.string(),
        points: z.array(Point3DSchema),
        description: z.string().optional(),
      }),
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addSurfacePoints"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("addSurfacePoints", {
            name: args.name,
            points: args.points,
            description: args.description,
          })
        ),
    },
    add_breakline: {
      action: "add_breakline",
      inputSchema: z.object({
        action: z.literal("add_breakline"),
        name: z.string(),
        breaklineType: z.enum(["standard", "wall", "proximity"]),
        points: z.array(Point3DSchema),
        description: z.string().optional(),
      }),
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addSurfaceBreakline"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("addSurfaceBreakline", {
            name: args.name,
            breaklineType: args.breaklineType,
            points: args.points,
            description: args.description,
          })
        ),
    },
    add_boundary: {
      action: "add_boundary",
      inputSchema: z.object({
        action: z.literal("add_boundary"),
        name: z.string(),
        boundaryType: z.enum(["show", "hide", "outer", "data_clip"]),
        boundaryPoints: z.array(Point2DSchema),
      }),
      capabilities: ["edit"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["addSurfaceBoundary"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("addSurfaceBoundary", {
            name: args.name,
            boundaryType: args.boundaryType,
            points: args.boundaryPoints,
          })
        ),
    },
    extract_contours: {
      action: "extract_contours",
      inputSchema: z.object({
        action: z.literal("extract_contours"),
        name: z.string(),
        minorInterval: z.number(),
        majorInterval: z.number(),
      }),
      capabilities: ["generate"],
      requiresActiveDrawing: true,
      safeForRetry: false,
      pluginMethods: ["extractSurfaceContours"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("extractSurfaceContours", {
            name: args.name,
            minorInterval: args.minorInterval,
            majorInterval: args.majorInterval,
          })
        ),
    },
    compute_volume: {
      action: "compute_volume",
      inputSchema: z.object({
        action: z.literal("compute_volume"),
        baseSurface: z.string(),
        comparisonSurface: z.string(),
      }),
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["computeSurfaceVolume"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("computeSurfaceVolume", {
            baseSurface: args.baseSurface,
            comparisonSurface: args.comparisonSurface,
          })
        ),
    },
    sample_grid: {
      action: "sample_grid",
      inputSchema: z.object({
        action: z.literal("sample_grid"),
        name: z.string(),
        minX: z.number(),
        minY: z.number(),
        maxX: z.number(),
        maxY: z.number(),
        spacing: z.number().positive(),
        outputPath: z.string().optional(),
      }),
      capabilities: ["query", "analyze", "export"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["sampleSurfaceGrid"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("sampleSurfaceGrid", {
            name: args.name,
            minX: args.minX,
            minY: args.minY,
            maxX: args.maxX,
            maxY: args.maxY,
            spacing: args.spacing,
            outputPath: args.outputPath,
          })
        ),
    },
    get_elevations: {
      action: "get_elevations",
      inputSchema: z.object({
        action: z.literal("get_elevations"),
        name: z.string(),
        xyPoints: z.array(Point2DSchema).min(1).max(20000),
      }),
      capabilities: ["query", "analyze"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      pluginMethods: ["getSurfaceElevations"],
      execute: async (args: any) =>
        await withApplicationConnection(async (c) =>
          await c.sendCommand("getSurfaceElevations", {
            name: args.name,
            points: args.xyPoints,
          })
        ),
    },
  },
  exposures: [
    {
      toolName: "civil3d_surface",
      displayName: "Civil 3D Surface",
      description:
        "Manage Civil 3D surfaces. Actions: list, get (by name), get_elevation (at X,Y), " +
        "get_statistics, create, delete, add_points, add_breakline, add_boundary, " +
        "extract_contours, compute_volume (between two surfaces), " +
        "sample_grid (regular grid of elevations; CSV to outputPath for large grids), " +
        "get_elevations (elevations at a list of XY points).",
      inputShape: canonicalInputShape,
      supportedActions: [
        "list",
        "get",
        "get_elevation",
        "get_statistics",
        "create",
        "delete",
        "add_points",
        "add_breakline",
        "add_boundary",
        "extract_contours",
        "compute_volume",
        "sample_grid",
        "get_elevations",
      ],
      resolveAction: (rawArgs) => ({
        action: String(rawArgs.action),
        args: rawArgs,
      }),
    },
  ],
};
