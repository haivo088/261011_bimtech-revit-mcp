import { z } from "zod";
import { defineTool, readOnly } from "./define.js";
import { elementFilter, elementId, paging, pagingNote, unitsNote } from "./schemas.js";

export const getElements = defineTool({
  name: "get_elements",
  method: "get_elements",
  title: "Find elements",
  description:
    "Find elements in the open model by category, view, level, or name, sorted by ID."
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
