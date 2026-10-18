import { z } from "zod";
import { defineTool, readOnly } from "./define.js";
import { elementFilter, elementId, paging, pagingNote, unitsNote } from "./schemas.js";

export const getElements = defineTool({
  name: "get_elements",
  method: "get_elements",
  title: "Find elements",
  description:
    "Find elements in the open model by category, category type, view, level, or name, sorted by ID."
    + " Use categoryType \"Model\" to skip annotations and internal elements."
    + " Each item has id, name, category, builtInCategory, typeName, and level."
    + " Use filters to narrow large models." + pagingNote,
  inputSchema: { ...elementFilter, ...paging },
  annotations: readOnly,
  timeoutMs: 60000,
});

export const getSelectedElements = defineTool({
  name: "get_selected_elements",
  method: "get_selected_elements",
  title: "Get selected elements",
  description:
    "List the elements currently selected in Revit, with the same fields as get_elements." + pagingNote,
  inputSchema: { ...paging },
  annotations: readOnly,
});

export const getElementParameters = defineTool({
  name: "get_element_parameters",
  method: "get_element_parameters",
  title: "Get element parameters",
  description:
    "Read the parameters of up to 50 elements. Pass parameterNames to return only those parameters,"
    + " which keeps the response small. IDs that do not exist are listed in notFound." + unitsNote,
  inputSchema: {
    elementIds: z.array(elementId).min(1).max(50).describe("Elements to read."),
    parameterNames: z.array(z.string().min(1).max(200)).max(100).optional()
      .describe("Only these parameters, matched by name ignoring case, e.g. [\"Mark\", \"Length\"]."),
    includeType: z.boolean().optional()
      .describe("Also return each element's type parameters. Default false."),
  },
  annotations: readOnly,
  timeoutMs: 30000,
});

export const proposeChangeType = defineTool({
  name: "propose_change_type",
  method: "propose_change_type",
  title: "Propose changing element types",
  description:
    "Plan changing existing elements to another type of the same category, without changing the model (instead of"
    + " send_code), e.g. walls written with the wrong wall type. Walls need a wall type, family instances (columns,"
    + " beams...) a family type of their category. One review item per current type, such as \"Walls · CW 102-85-100p"
    + " → BTC-VachKC_300 (14)\"; a wall whose thickness changes says so. Elements already of that type are skipped;"
    + " elements of another category, or that Revit does not allow on that type, are refused and listed in 'refused'."
    + " Opens the review window and returns a planId at once; then get_plan_status and commit_plan (one Ctrl+Z).",
  inputSchema: {
    elementIds: z.array(elementId).min(1).max(1000).describe("Elements whose type to change."),
    typeId: elementId.describe("Target type, e.g. a wall type ID from scan_cad_walls or get_elements with includeTypes=true."),
  },
  // Saves a plan file but never changes the model.
  annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: false },
  timeoutMs: 60000,
});
