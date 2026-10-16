import { z } from "zod";
import { defineTool, readOnly, type RevitTool } from "./define.js";

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

export const devTools: readonly RevitTool[] = [
  runRevitQuery,
];
