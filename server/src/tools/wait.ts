import { z } from "zod";
import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { config } from "../config.js";
import { callRevit, RevitError } from "../revit-client.js";

// wait_for_plan lives on the server only: it asks the add-in for the plan's status every few seconds (get_plan_status)
// until the person decides in the Revit review window, so Claude carries on without the person coming back to chat.
// The waiting happens here, never on Revit's main thread, so Revit stays responsive.

const planId = z.string().regex(/^pln_[0-9]{8}_[0-9a-f]{6}$/)
  .describe("Plan ID returned by a propose_* tool, e.g. \"pln_20261014_8f3a2c\".");

interface PlanStatus {
  planId: string;
  status: "InReview" | "Approved" | "Committed" | "Stale" | "Cancelled";
  approvalId: string | null;
  summary?: unknown;
}

// Approved but not written after this long: the "write on approve" setting is off, so Claude commits.
const approvedGraceMs = 10000;
// Revit is busy (e.g. writing the plan) or a dialog is open: keep waiting, but not forever.
const busyCodes = new Set([-32003]);

export const waitForPlanName = "wait_for_plan";

export function registerWaitForPlan(server: McpServer, call: typeof callRevit = callRevit): void {
  server.registerTool(waitForPlanName, {
    title: "Wait for the user's review",
    description:
      "Wait, without asking the user to come back to chat, until they decide a plan in the Revit review window. Call it"
      + " right after a propose_* tool (or send_code with write=true) instead of ending your turn. Returns when the plan"
      + " is Committed (the window wrote it: read the results with commit_plan or get_plan and go on with the next step"
      + " of what the user asked), Approved but not written (the Revit setting to write on approve is off: call"
      + " commit_plan with the approvalId), Cancelled or Stale (stop and tell the user), or when timeoutSeconds"
      + " pass (default 900: tell the user the plan is still waiting). Blocked items still need the user's answer in"
      + " chat: ask before waiting. Reads only; the plan's status is checked every few seconds.",
    inputSchema: {
      planId,
      timeoutSeconds: z.number().int().min(10).max(3600).optional()
        .describe("Give up after this many seconds (default 900)."),
    },
    annotations: { title: "Wait for the user's review", readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  }, async ({ planId: id, timeoutSeconds }, extra): Promise<CallToolResult> => {
    const started = Date.now();
    const deadline = started + (timeoutSeconds ?? 900) * 1000;
    let approvedAt: number | undefined;
    let last: PlanStatus | undefined;
    let polls = 0;
    const progressToken = extra._meta?.progressToken;

    while (!extra.signal.aborted) {
      try {
        last = await call("get_plan_status", { planId: id }) as PlanStatus;
      } catch (error) {
        if (!(error instanceof RevitError && busyCodes.has(error.code))) return failure(error);
      }
      polls++;
      const waited = Math.round((Date.now() - started) / 1000);
      if (last) {
        if (last.status === "Committed" || last.status === "Cancelled" || last.status === "Stale")
          return done(last, waited, next(last.status));
        if (last.status === "Approved") {
          approvedAt ??= Date.now();
          if (Date.now() - approvedAt >= approvedGraceMs)
            return done(last, waited, "The user approved but the plan is not written yet (write on approve is off): call commit_plan with this approvalId.");
        } else {
          approvedAt = undefined;
        }
      }
      if (Date.now() >= deadline)
        return done(last, waited, "The user has not decided yet. Tell them the plan is still waiting in the Revit review window; do not commit.", true);
      // Progress keeps the client from timing out while the person reviews.
      if (progressToken !== undefined)
        await extra.sendNotification({
          method: "notifications/progress",
          params: { progressToken, progress: polls, message: `Đang chờ duyệt trong Revit (${waited} s)` },
        }).catch(() => {});
      await sleep(config.waitPollMs, extra.signal);
    }
    return done(last, Math.round((Date.now() - started) / 1000), "Waiting was stopped.", true);
  });
}

function next(status: PlanStatus["status"]): string {
  switch (status) {
    case "Committed":
      return "The review window wrote the plan. Read what was written (commit_plan with the approvalId returns the stored"
        + " results without writing again) and go on with the next step.";
    case "Cancelled":
      return "The user cancelled the plan. Stop and ask them how to go on.";
    default:
      return "The plan is stale: the model changed before it was written. Propose again.";
  }
}

function done(status: PlanStatus | undefined, waitedSeconds: number, nextStep: string, timedOut = false): CallToolResult {
  return {
    isError: false,
    content: [{ type: "text", text: JSON.stringify({ ...status, waitedSeconds, timedOut, next: nextStep }, null, 2) }],
  };
}

function failure(error: unknown): CallToolResult {
  const text = error instanceof RevitError && error.data !== undefined
    ? `${error.message}\nDetails: ${JSON.stringify(error.data)}`
    : error instanceof Error ? error.message : String(error);
  return { isError: true, content: [{ type: "text", text }] };
}

function sleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, ms);
    signal.addEventListener("abort", () => { clearTimeout(timer); resolve(); }, { once: true });
  });
}
