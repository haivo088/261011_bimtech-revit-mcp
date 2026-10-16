import { fileURLToPath } from "node:url";
import { z } from "zod";
import { defineTool, readOnly, type RevitTool } from "./define.js";
import { elementId } from "./schemas.js";

// Developer-only tools, backed by Bimtech.RevitMcp.DevTools.dll (devtools/). The server registers them only
// when BIMTECH_DEV=1, and the add-in only knows them when that DLL is installed next to it.

// commandsets/ at the repository root (committed); BIMTECH_COMMANDSETS_DIR overrides it.
export const commandSetRoot = process.env.BIMTECH_COMMANDSETS_DIR
  ?? fileURLToPath(new URL("../../../commandsets/", import.meta.url));
// Every send_code run, one JSON line each, in logs/ at the repository root (gitignored).
export const historyFile = process.env.BIMTECH_CODE_HISTORY
  ?? fileURLToPath(new URL("../../../logs/code-runs.jsonl", import.meta.url));

const scriptName = z.string().regex(/^[a-z0-9][a-z0-9-]*\/[a-z0-9][a-z0-9-]*$/).max(120);

export const sendCode = defineTool({
  name: "send_code",
  method: "send_code",
  title: "Send C# code to Revit (developer)",
  description:
    "Developer tool. Compile and run C# in the open Revit model: a new snippet (code) or a saved command-set script"
    + " (script \"set/name\" + args; check command_set list before writing new code). The snippet is the body of"
    + " `static object Run(QueryContext ctx)`: `doc`, `uidoc`, `view` and `ctx` are in scope, System.Linq,"
    + " Autodesk.Revit.DB and DB.Structure are imported, and it ends with `return <value>;`. Return anonymous objects"
    + " or lists, not Revit elements. ctx.Print(x) logs; QueryContext.Mm(feet)/QueryContext.Feet(mm) convert (static); arguments are read with"
    + " ctx.Str/Num/Bool/Id/Strs/Ids(\"name\") and ctx.Has(\"name\")."
    + " write=false (default): runs inside a transaction group that is always rolled back, so the model never"
    + " changes; use it to explore and check. write=true (needs purpose): the code runs inside a Transaction that is"
    + " already open, so it must not start its own (SubTransaction is fine); it is previewed and rolled back, the"
    + " counts of elements it would create, change and delete come back, and a plan opens in the review window with"
    + " the whole code. Nothing changes until the user approves and you call commit_plan, which runs exactly that code"
    + " as one undo step. Loops stop after timeoutSeconds and recursion after 1000 calls, so a mistake cannot freeze"
    + " or crash Revit. Every run is logged; when the same kind of job comes back, propose saving it with command_set.",
  inputSchema: {
    code: z.string().min(1).max(100000).optional().describe("C# statements ending with a return. Not with script."),
    script: scriptName.optional().describe("Command-set script \"set/name\" to run instead of code."),
    args: z.record(z.string(), z.unknown()).optional()
      .describe("Arguments: checked against the script's parameters; new code reads them with ctx.Str(\"x\")..."),
    write: z.boolean().optional()
      .describe("true: preview and propose a plan that changes the model. Default false, or the script's own setting."),
    purpose: z.string().min(1).max(300).optional()
      .describe("One sentence the reviewer reads: what the code changes. Required with write=true for new code."),
    timeoutSeconds: z.number().int().min(1).max(110).optional()
      .describe("Stop the code's loops after this many seconds (default 30)."),
    maxResultChars: z.number().int().min(1000).max(1000000).optional()
      .describe("Cut the result JSON after this many characters (default 50000)."),
  },
  // write=false never changes the model, and write=true only proposes a plan the user reviews.
  annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: false },
  timeoutMs: 120000,
  toParams: (input) => ({ ...input, commandSetRoot, historyFile }),
});

export const commandSet = defineTool({
  name: "command_set",
  method: "command_set",
  title: "Command sets of saved code (developer)",
  description:
    "Developer tool. Command sets are send_code snippets that proved useful, saved with a name and typed parameters"
    + " in the repository's commandsets/ folder. action \"list\": every set and script with description, write flag"
    + " and parameters. action \"save\": keep a snippet as script \"set/name\" (it must compile), only after the user"
    + " agreed to its name, parameters and description; an existing script is replaced only with overwrite=true."
    + " action \"history\": the latest send_code runs, newest first, to spot jobs that keep coming back and propose"
    + " packaging them.",
  inputSchema: {
    action: z.enum(["list", "save", "history"]),
    script: scriptName.optional().describe("save: \"set/name\", lowercase letters, digits and '-'."),
    description: z.string().min(1).max(500).optional().describe("save: what the script does, in one sentence."),
    setDescription: z.string().min(1).max(500).optional().describe("save: description of the set, when it is new."),
    code: z.string().min(1).max(100000).optional().describe("save: the snippet, reading its parameters with ctx.Str(...)."),
    write: z.boolean().optional().describe("save: true when the script changes the model (it then runs through review)."),
    params: z.array(z.object({
      name: z.string().regex(/^[A-Za-z][A-Za-z0-9]*$/).max(40),
      type: z.enum(["string", "number", "boolean", "elementId", "string[]", "elementId[]"]),
      description: z.string().max(500).optional(),
      default: z.unknown().optional(),
      required: z.boolean().optional(),
    })).max(40).optional().describe("save: the script's parameters."),
    notes: z.array(z.string().max(500)).max(50).optional().describe("save: where and why it was used."),
    overwrite: z.boolean().optional().describe("save: replace an existing script."),
    count: z.number().int().min(1).max(500).optional().describe("history: how many runs (default 30)."),
  },
  // Never touches the model; save only writes files in the repository (like export_cad_case writes cases/).
  annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: false },
  timeoutMs: 60000,
  toParams: (input) => ({ ...input, commandSetRoot, historyFile }),
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
    + " model are saved as the truth: export after the user built and approved the grids of this drawing. For the level"
    + " (levelId, else the view's level), the columns and walls reaching it are saved as beam supports and its beams as"
    + " the beam truth, so beam recognition replays on the case: export a beam plan after its beams were built and"
    + " checked (missing ones added, wrong ones fixed). The DWG file itself is also read from disk (a linked CAD's own"
    + " file, or dwgPath for an imported one) for its hatches and texts, which Revit does not expose; they are added"
    + " only when the file, placed by matching its lines with the CAD's lines in Revit, reproduces at least 70% of"
    + " them. The result's dwg part reports the placement, match rate and hatches per layer and pattern. A large DWG"
    + " takes about a minute to read.",
  inputSchema: {
    cadId,
    viewId,
    levelId: elementId.optional().describe("Level whose supports and beams are saved. Default: the plan view's level."),
    notes: z.string().max(2000).optional().describe("Free notes about the drawing, e.g. its source or quirks."),
    includeTruth: z.boolean().optional().describe("Save the model's grids as truth (default true)."),
    maxEntities: z.number().int().min(1).max(2000000).optional().describe("Stop after this many entities (default 300000)."),
    dwgPath: z.string().min(1).max(1024).optional()
      .describe("Absolute path of the DWG/DXF file of an imported (not linked) CAD. A linked CAD's own file is used by default."),
    readDwg: z.boolean().optional().describe("Read the DWG file for hatches and texts (default true)."),
  },
  annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: false },
  timeoutMs: 300000,
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

export const analyzeCad = defineTool({
  name: "analyze_cad",
  method: "analyze_cad",
  title: "Analyze a CAD drawing (developer)",
  description:
    "Developer tool. Fingerprint one CAD import so the whole drawing fits in one reply: extent, totals, and per layer"
    + " the line patterns and colors, line counts and lengths, share of horizontal/vertical lines, circle diameters,"
    + " rectangle sizes (\"300x500\"), and widths of parallel line pairs (beams, walls); the most frequent texts; and"
    + " the best grid candidate. Use it to propose which layer holds piles, caps, columns, beams, walls or grids,"
    + " then confirm with the user.",
  inputSchema: {
    cadId,
    viewId,
    maxLayers: z.number().int().min(1).max(500).optional().describe("Layers to return, busiest first (default 60)."),
  },
  annotations: readOnly,
  timeoutMs: 120000,
});

export const devTools: readonly RevitTool[] = [
  sendCode,
  commandSet,
  exportCadCase,
  runRecognition,
  analyzeCad,
];
