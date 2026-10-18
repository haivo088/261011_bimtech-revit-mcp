import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { ToolAvailability } from "../availability.js";
import { config } from "../config.js";
import { registerRevitTools, type RevitTool } from "./define.js";
import { devTools } from "./dev.js";
import { pingRevit } from "./diagnostics.js";
import { getDocumentInfo, getLevels, listCategories } from "./document.js";
import { getElementParameters, getElements, getSelectedElements, proposeChangeType } from "./elements.js";
import { proposeSetParameters } from "./parameters.js";
import { cancelPlan, commitPlan, getPlan, getPlanStatus, openPlanReview } from "./plans.js";
import {
  listCadSources, loadDefaultFamily, proposeBeams, proposeColumns, proposeFloors, proposeGrids, proposeLevels,
  proposePiles, proposeWalls, suggestLevelsFromFolder,
  scanCadColumns, scanCadPiles, scanCadWalls, undoBatch,
} from "./structural.js";
import { registerWaitForPlan, waitForPlanName } from "./wait.js";

// The list of tools Claude can call. To add a tool: define it in a file in this
// folder, add it here, and add the matching command in addin/Commands/CommandRegistry.cs.
export const allTools: readonly RevitTool[] = [
  pingRevit,
  getDocumentInfo,
  proposeSetParameters,
  getPlan,
  getPlanStatus,
  openPlanReview,
  cancelPlan,
  commitPlan,
  getLevels,
  listCategories,
  getElements,
  getSelectedElements,
  getElementParameters,
  proposeChangeType,
  listCadSources,
  suggestLevelsFromFolder,
  proposeLevels,
  proposeGrids,
  scanCadWalls,
  proposeWalls,
  proposeFloors,
  loadDefaultFamily,
  scanCadColumns,
  proposeColumns,
  proposeBeams,
  scanCadPiles,
  proposePiles,
  undoBatch,
];

// Tools of the server alone, built on add-in commands listed above: always visible.
export const serverOnlyToolNames: readonly string[] = [waitForPlanName];

// Developer tools (dev.ts) are added only when BIMTECH_DEV=1.
export function activeTools(): readonly RevitTool[] {
  return config.devTools ? [...allTools, ...devTools] : allTools;
}

export function registerTools(server: McpServer): ToolAvailability {
  // Server-only: waits on get_plan_status, so it has no add-in command of its own.
  registerWaitForPlan(server);
  // The callback runs only after registration finishes, so "availability" is always set by then.
  const availability: ToolAvailability = new ToolAvailability(
    registerRevitTools(server, activeTools(), () => { void availability.sync(); }),
  );
  return availability;
}
