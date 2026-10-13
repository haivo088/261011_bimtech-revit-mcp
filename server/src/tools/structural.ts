import { z } from "zod";
import { defineTool, readOnly } from "./define.js";
import { elementId } from "./schemas.js";

// CAD-to-structure workflow. Each element kind follows the same steps:
//   list_cad_sources → scan_cad_<kind> (layers) → scan_cad_<kind> (groups, scanId)
//   → place_<kind> dryRun=true → user confirms → place_<kind> dryRun=false → undo_batch if needed.
// Geometry stays in the add-in; tools exchange scanId/batchId and summaries only.

const regionMm = z.object({
  minX: z.number(), minY: z.number(), maxX: z.number(), maxY: z.number(),
}).describe("Optional plan rectangle in millimeters (model coordinates); only outlines whose center lies inside are kept.");

export const listCadSources = defineTool({
  name: "list_cad_sources",
  method: "list_cad_sources",
  title: "List CAD sources",
  description:
    "List CAD imports and links in the open model with their IDs, and whether the active view is a plan."
    + " Start here before scanning a CAD drawing.",
  inputSchema: {},
  annotations: readOnly,
});

export const scanCadColumns = defineTool({
  name: "scan_cad_columns",
  method: "scan_cad_columns",
  title: "Scan CAD for columns",
  description:
    "Read rectangular column outlines from a CAD drawing as shown in a plan view."
    + " Call first without layers to see which layers contain outlines and ask the user which are columns."
    + " Call again with layers to group columns by size (key like \"500x300\" in mm), get type suggestions"
    + " (exactType or nearestType), and a scanId for place_columns. Changes nothing in the model.",
  inputSchema: {
    cadId: elementId.describe("CAD import or link ID from list_cad_sources."),
    viewId: elementId.optional().describe("Plan view showing the CAD. Defaults to the CAD's own view if it is view-specific, else the active view."),
    layers: z.array(z.string().min(1).max(255)).max(50).optional()
      .describe("CAD layer names holding column outlines, exactly as returned by the first call."),
    region: regionMm.optional(),
  },
  annotations: readOnly,
  timeoutMs: 120000,
});

export const loadDefaultColumnFamily = defineTool({
  name: "load_default_column_family",
  method: "load_default_column_family",
  title: "Load default column family",
  description:
    "Load the company concrete column family (BTC-CotCN_BeTong, with b/h size parameters) into the model,"
    + " so place_columns can create sized types in it via templateFamilyId. Use when scan_cad_columns reports"
    + " no column type with b/h parameters. Does nothing if already loaded. Confirm with the user first.",
  inputSchema: {},
  annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true },
  timeoutMs: 60000,
});

export const placeColumns = defineTool({
  name: "place_columns",
  method: "place_columns",
  title: "Place columns from scan",
  description:
    "Place structural columns from a scan between two levels, rotated as in the CAD; positions that already"
    + " have a column of the same family are skipped."
    + " dryRun defaults to true and only reports the plan: always show it to the user and get confirmation"
    + " before calling again with dryRun=false. A real run is one undo step in Revit and returns a batchId"
    + " for undo_batch. Each size group uses: its entry in 'types', else an existing type with matching b/h,"
    + " else a new type created in templateFamilyId.",
  inputSchema: {
    scanId: z.string().min(1).max(64).describe("scanId from scan_cad_columns."),
    baseLevelId: elementId.describe("Bottom level of the columns."),
    topLevelId: elementId.describe("Top level of the columns; must be higher than the base level."),
    types: z.record(z.string(), elementId).optional()
      .describe("Type to use per group key, e.g. {\"500x300\": \"12345\"}. Overrides automatic matching."),
    templateFamilyId: elementId.optional()
      .describe("Column family (familyId from a scan type) in which to create types for groups with no matching type."),
    dryRun: z.boolean().optional().describe("Default true: report only. false: modify the model."),
  },
  annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false },
  timeoutMs: 120000,
});

export const undoBatch = defineTool({
  name: "undo_batch",
  method: "undo_batch",
  title: "Undo a batch",
  description:
    "Delete the elements created by one write call, identified by its batchId. Types created by that call"
    + " are deleted only if nothing uses them. Confirm with the user first.",
  inputSchema: {
    batchId: z.string().min(1).max(64).describe("batchId returned by a write tool such as place_columns."),
  },
  annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false },
  timeoutMs: 60000,
});
