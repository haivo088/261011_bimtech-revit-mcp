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
    "Read a plan's status and item counts. Call it when the user says they finished reviewing (to wait for their decision without them coming back to chat, use wait_for_plan). "
    + "Statuses: InReview, Approved, Committed, Stale, Cancelled. Only the user can approve a plan, "
    + "in the Revit review window; approvalId is set once they do. Committed right after their approval means the "
    + "review window already wrote it (Revit setting \"Bấm Duyệt kế hoạch là ghi vào mô hình ngay\", on by default): "
    + "nothing is left to commit; carry on with the next step.",
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

export const openPlanReview = defineTool({
  name: "open_plan_review",
  method: "open_plan_review",
  title: "Open plan review window",
  description:
    "Open the review window in Revit for a plan, e.g. after the user closed it. Returns at once; "
    + "the user approves or rejects items there and says when they are done. The model is not changed.",
  inputSchema: { planId },
  // Opens a window but never changes the model.
  annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
});

const approvalId = z.string().regex(/^apr_[0-9a-f]{16}$/)
  .describe("approvalId from get_plan_status after the user approved, e.g. \"apr_3f9c0e7a1b2d4c68\". Never guess one.");

export const commitPlan = defineTool({
  name: "commit_plan",
  method: "commit_plan",
  title: "Commit approved plan",
  description:
    "Write the items the user approved in the Revit review window into the model. Call it after the user says they "
    + "finished reviewing and get_plan_status shows status Approved, or at once when a propose result says "
    + "autoApproved: true (the user turned on auto-commit in the Revit settings and every item was sure, so there is "
    + "nothing to review); pass that approvalId. "
    + "Rejected items are never written. The whole commit is one undo step in Revit. Errors: -32022 the model "
    + "changed since the plan was proposed (plan becomes Stale; propose again), -32023 the approvalId is not the "
    + "latest (read get_plan_status again). Calling it again on a committed plan returns the same results, e.g. a "
    + "plan the review window already wrote when the user approved it.",
  inputSchema: { planId, approvalId },
  // Plans may hold items that overwrite or delete, so treat a commit as destructive.
  annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: true },
  timeoutMs: 120000,
});

export const cancelPlan = defineTool({
  name: "cancel_plan",
  method: "cancel_plan",
  title: "Cancel plan",
  description: "Abandon a plan that has not been committed. The Revit model is not changed.",
  inputSchema: { planId },
  annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true },
});
