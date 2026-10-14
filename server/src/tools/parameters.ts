import { z } from "zod";
import { defineTool } from "./define.js";

export const proposeSetParameters = defineTool({
  name: "propose_set_parameters",
  method: "propose_set_parameters",
  title: "Propose setting a parameter",
  description:
    "Plan setting one instance parameter to one value on many elements, without changing the model. "
    + "Uses the current Revit selection unless elementIds is given. Elements that already have the value "
    + "are skipped; elements that cannot take it become Blocked items with a reason. "
    + "Opens the review window in Revit and returns a planId and a summary at once. Tell the user the counts, "
    + "ask them to review in Revit and say when they are done, then call get_plan_status.",
  inputSchema: {
    parameter: z.string().min(1).max(256).describe("Parameter name as shown in Revit, e.g. \"Comments\"."),
    value: z.string().max(4096)
      .describe("New value as text. Numbers use the model's units, e.g. \"300\" or \"300 mm\"."),
    elementIds: z.array(z.string().regex(/^-?\d+$/)).min(1).max(1000).optional()
      .describe("Element IDs as strings. Omit to use the current selection."),
  },
  // Saves a plan file but never changes the model.
  annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: false },
});
