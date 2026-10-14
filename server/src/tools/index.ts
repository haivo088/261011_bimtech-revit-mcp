import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { registerRevitTools, type RevitTool } from "./define.js";
import { pingRevit } from "./diagnostics.js";
import { getDocumentInfo } from "./document.js";
import { proposeSetParameters } from "./parameters.js";
import { cancelPlan, commitPlan, getPlan, getPlanStatus, openPlanReview } from "./plans.js";

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
];

export function registerTools(server: McpServer): void {
  registerRevitTools(server, allTools);
}
