import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { registerRevitTools, type RevitTool } from "./define.js";
import { pingRevit } from "./diagnostics.js";
import { getDocumentInfo, getLevels, listCategories } from "./document.js";
import { getElementParameters, getElements, getSelectedElements } from "./elements.js";

// The list of tools Claude can call. To add a tool: define it in a file in this
// folder, add it here, and add the matching command in addin/Commands/CommandRegistry.cs.
export const allTools: readonly RevitTool[] = [
  pingRevit,
  getDocumentInfo,
  getLevels,
  listCategories,
  getElements,
  getSelectedElements,
  getElementParameters,
];

export function registerTools(server: McpServer): void {
  registerRevitTools(server, allTools);
}
