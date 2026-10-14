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
    + "Returns a planId and a summary; tell the user the counts and that the plan waits for their review.",
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
