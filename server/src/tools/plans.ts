import { z } from "zod";
import { defineTool } from "./define.js";

// Plans hold proposed model changes until a person approves them in Revit.
// See docs/04-architecture.md.
const planId = z.string().regex(/^pln_[0-9]{8}_[0-9a-f]{6}$/)
  .describe("Plan ID returned by a propose_* tool, e.g. \"pln_20261014_8f3a2c\".");

export const getPlanStatus = defineTool({
  name: "get_plan_status",
  method: "get_plan_status",
  title: "Get plan status",
  description:
    "Read a plan's status and item counts. Call it when the user says they finished reviewing. "
    + "Statuses: InReview, Approved, Committed, Stale, Cancelled. Only the user can approve a plan, in Revit.",
  inputSchema: { planId },
  annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
});

export const getPlan = defineTool({
  name: "get_plan",
  method: "get_plan",
  title: "Get plan",
  description:
    "Read a plan and a page of its items, each with params, tier (Auto, Review, Blocked) and reasons. "
    + "Use the filters to answer questions such as which items are blocked and why. "
    + "Quote the stored reasons; do not invent new ones.",
  inputSchema: {
    planId,
    itemStatus: z.enum(["Pending", "Approved", "Rejected", "Applied", "Failed"]).optional()
      .describe("Only items with this status."),
    tier: z.enum(["Auto", "Review", "Blocked"]).optional().describe("Only items with this tier."),
    itemIds: z.array(z.string().max(64)).max(1000).optional().describe("Only these items, e.g. [\"E12345\"]."),
    offset: z.number().int().min(0).optional().describe("Items to skip. Default 0."),
    limit: z.number().int().min(1).max(200).optional().describe("Items per page. Default 50."),
  },
  annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
});

export const cancelPlan = defineTool({
  name: "cancel_plan",
  method: "cancel_plan",
  title: "Cancel plan",
  description: "Abandon a plan that has not been committed. The Revit model is not changed.",
  inputSchema: { planId },
  annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true },
});
