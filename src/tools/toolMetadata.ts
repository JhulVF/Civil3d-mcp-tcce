/**
 * Shared types and interfaces for the domain-based tool architecture.
 */

/** Tool capability tags for categorization */
export type ToolCapability =
  | "query"
  | "inspect"
  | "create"
  | "edit"
  | "delete"
  | "analyze"
  | "generate"
  | "manage"
  | "import"
  | "export";

/** Domain categories for Civil 3D objects */
export type ToolDomain =
  | "plugin"
  | "drawing"
  | "surface"
  | "alignment"
  | "profile"
  | "corridor"
  | "section"
  | "pipe"
  | "point"
  | "grading"
  | "parcel"
  | "geometry"
  | "block"
  | "hatch"
  | "road"
  | "style"
  | "earthwork"
  | "label"
  | "quantity"
  | "survey"
  | "project"
  | "assembly"
  | "workflow";

/** Catalog entry describing a registered tool */
export interface ToolCatalogEntry {
  toolName: string;
  displayName: string;
  description: string;
  domain: ToolDomain;
  capabilities: ToolCapability[];
  operations?: string[];
  pluginMethods?: string[];
  requiresActiveDrawing: boolean;
  safeForRetry: boolean;
  status: "implemented" | "planned";
}
