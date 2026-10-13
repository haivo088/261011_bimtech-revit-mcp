import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { ToolAvailability } from "../availability.js";
import { registerRevitTools, type RevitTool } from "./define.js";
import { pingRevit } from "./diagnostics.js";
import { getDocumentInfo, getLevels, listCategories } from "./document.js";
import { getElementParameters, getElements, getSelectedElements } from "./elements.js";
import {
  listCadSources, loadDefaultFamily, placeBeams, placeColumns, placePiles, scanCadBeams, scanCadColumns, scanCadPiles, undoBatch,
} from "./structural.js";

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
  listCadSources,
  loadDefaultFamily,
  scanCadColumns,
  placeColumns,
  scanCadBeams,
  placeBeams,
  scanCadPiles,
  placePiles,
  undoBatch,
];

export function registerTools(server: McpServer): ToolAvailability {
  // The callback runs only after registration finishes, so "availability" is always set by then.
  const availability: ToolAvailability = new ToolAvailability(
    registerRevitTools(server, allTools, () => { void availability.sync(); }),
  );
  return availability;
}
