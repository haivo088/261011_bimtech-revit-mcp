import { z } from "zod";
import { defineTool } from "./define.js";

export const pingRevit = defineTool({
  name: "ping_revit",
  method: "ping",
  title: "Ping Revit",
  description:
    "Check the BIMTECH Revit bridge and report the Revit and bridge versions. "
    + "Pass an optional message to confirm that parameters reach Revit; it is echoed back.",
  inputSchema: {
    message: z.string().max(200).optional().describe("Text the add-in returns unchanged."),
  },
  annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true },
  timeoutMs: 5000,
});
