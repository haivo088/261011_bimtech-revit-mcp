import { defineTool } from "./define.js";

export const getDocumentInfo = defineTool({
  name: "get_document_info",
  method: "get_document_info",
  title: "Get document info",
  description:
    "Read the title, path, type, and active view of the open Revit model. "
    + "activeView is null when no view is active.",
  inputSchema: {},
  annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
});
