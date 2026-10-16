import { fileURLToPath } from "node:url";
import { z } from "zod";
import { defineTool, readOnly, type RevitTool } from "./define.js";
import { elementId } from "./schemas.js";

// Developer-only tools, backed by Bimtech.RevitMcp.DevTools.dll (devtools/). The server registers them only
// when BIMTECH_DEV=1, and the add-in only knows them when that DLL is installed next to it.

export const runRevitQuery = defineTool({
  name: "run_revit_query",
  method: "run_revit_query",
  title: "Run a C# query in Revit (developer)",
  description:
    "Developer tool. Compile and run a C# snippet against the open Revit model to explore data, e.g. what a CAD"
    + " import contains. The snippet is the body of `static object Run(QueryContext ctx)`: `doc`, `uidoc`, `view` and"
    + " `ctx` are in scope, System.Linq and Autodesk.Revit.DB are imported, and it should end with `return <value>;`."
    + " Return anonymous objects or lists of numbers and strings, not Revit elements. ctx.Print(x) adds a log line;"
    + " ctx.Mm(feet) and ctx.Feet(mm) convert lengths; long loops should call ctx.Cancellation.ThrowIfCancellationRequested()."
    + " Everything runs inside a transaction group that is always rolled back, so the model is never changed."
    + " Compile errors come back with line numbers of the snippet.",
  inputSchema: {
    code: z.string().min(1).max(100000).describe("C# statements ending with a return."),
    maxResultChars: z.number().int().min(1000).max(1000000).optional()
      .describe("Cut the result JSON after this many characters (default 50000)."),
  },
  annotations: readOnly,
  timeoutMs: 120000,
});

// cases/ at the repository root (gitignored); BIMTECH_CASES_DIR overrides it.
export const caseRoot = process.env.BIMTECH_CASES_DIR ?? fileURLToPath(new URL("../../../cases/", import.meta.url));

const cadId = elementId.describe("CAD import or link ID from list_cad_sources.");
const viewId = elementId.optional()
  .describe("Plan view showing the CAD. Defaults to the CAD's own view if it is view-specific, else the active view.");

export const exportCadCase = defineTool({
  name: "export_cad_case",
  method: "export_cad_case",
  title: "Export a CAD case (developer)",
  description:
    "Developer tool. Save one CAD import as plain data (lines, arcs, circles, polylines with layer, line pattern and"
    + " color; Text Notes of the view) to cases/<caseId>/case.json on this machine, for developing and"
    + " regression-testing recognition rules outside Revit. With includeTruth (default), the grids already in the"
    + " model are saved as the truth: export after the user built and approved the grids of this drawing.",
  inputSchema: {
    cadId,
    viewId,
    notes: z.string().max(2000).optional().describe("Free notes about the drawing, e.g. its source or quirks."),
    includeTruth: z.boolean().optional().describe("Save the model's grids as truth (default true)."),
    maxEntities: z.number().int().min(1).max(2000000).optional().describe("Stop after this many entities (default 300000)."),
  },
  annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: false },
  timeoutMs: 120000,
  toParams: (input) => ({ ...input, caseRoot }),
});

export const runRecognition = defineTool({
  name: "run_recognition",
  method: "run_recognition",
  title: "Run recognition on a CAD (developer)",
  description:
    "Developer tool. Run the grid recognition rules on one CAD import: which graphics style looks like grid lines"
    + " (line pattern first, then grid bubbles, then layer name) with the evidence per style, the grids found with"
    + " names read from their bubbles, and warnings. If the model already has grids, they are compared as the truth"
    + " (matched, wrong names, missing, extra).",
  inputSchema: {
    cadId,
    viewId,
    compareWithModel: z.boolean().optional().describe("Compare with the model's grids (default true)."),
  },
  annotations: readOnly,
  timeoutMs: 120000,
});

export const devTools: readonly RevitTool[] = [
  runRevitQuery,
  exportCadCase,
  runRecognition,
];
