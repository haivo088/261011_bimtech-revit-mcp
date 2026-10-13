import { defineTool, readOnly } from "./define.js";

export const getDocumentInfo = defineTool({
  name: "get_document_info",
  method: "get_document_info",
  title: "Get document info",
  description:
    "Read the title, path, type, and active view of the open Revit model. "
    + "activeView is null when no view is active.",
  inputSchema: {},
  annotations: readOnly,
});

export const getLevels = defineTool({
  name: "get_levels",
  method: "get_levels",
  title: "Get levels",
  description: "List the model's levels with ID, name, and elevation in millimeters, lowest first.",
  inputSchema: {},
  annotations: readOnly,
});

export const listCategories = defineTool({
  name: "list_categories",
  method: "list_categories",
  title: "List categories",
  description:
    "Count placed elements per category in the open model, most common first."
    + " Use builtInCategory values as the categories filter of get_elements.",
  inputSchema: {},
  annotations: readOnly,
  timeoutMs: 60000,
});
