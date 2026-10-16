import { z } from "zod";
import { defineTool, readOnly } from "./define.js";
import { elementId } from "./schemas.js";

// CAD-to-structure workflow. Each element kind follows the same steps:
//   list_cad_sources → scan_cad_<kind> (layers) → scan_cad_<kind> (groups, scanId)
//   → place_<kind> dryRun=true → user confirms → place_<kind> dryRun=false → undo_batch if needed.
// Geometry stays in the add-in; tools exchange scanId/batchId and summaries only.

const regionMm = z.object({
  minX: z.number(), minY: z.number(), maxX: z.number(), maxY: z.number(),
}).describe("Optional plan rectangle in millimeters (model coordinates); only outlines whose center lies inside are kept.");

const write = { readOnlyHint: false, destructiveHint: false, idempotentHint: false };
const viewId = elementId.optional()
  .describe("Plan view showing the CAD. Defaults to the CAD's own view if it is view-specific, else the active view.");
const layerList = (what: string) => z.array(z.string().min(1).max(255)).max(50).optional()
  .describe(`CAD layer names holding ${what}, exactly as returned by the first call.`);
const typeMap = (example: string) => z.record(z.string(), elementId).optional()
  .describe(`Type to use per group key, e.g. {"${example}": "12345"}. Overrides automatic matching.`);
const dryRun = z.boolean().optional().describe("Default true: report only. false: modify the model.");

export const listCadSources = defineTool({
  name: "list_cad_sources",
  method: "list_cad_sources",
  title: "List CAD sources",
  description:
    "List CAD imports and links in the open model with their IDs, and whether the active view is a plan."
    + " Start here before scanning a CAD drawing.",
  inputSchema: {},
  annotations: readOnly,
});

// Saves a plan file and opens the review window, but never changes the model.
const propose = { readOnlyHint: true, destructiveHint: false, idempotentHint: false };

export const proposeLevels = defineTool({
  name: "propose_levels",
  method: "propose_levels",
  title: "Propose levels with their CAD",
  description:
    "Plan setting up storeys, without changing the model. Each storey becomes one review item that creates the level"
    + " (or reuses one with the same name and elevation), its structural plan \"KC - <name>\", and imports or links"
    + " that storey's DWG into the plan at the origin. Storeys that are already complete and have no cadFile are skipped;"
    + " problems (missing file, existing level at another elevation, duplicate names) become Blocked items."
    + " Opens the review window in Revit and returns a planId at once. Tell the user the counts, ask them to review in"
    + " Revit and say when they are done, then call get_plan_status and commit_plan."
    + " After the commit, call list_cad_sources and propose_grids to build grids and align the CAD.",
  inputSchema: {
    levels: z.array(z.object({
      name: z.string().min(1).max(255).describe("Level name, e.g. \"Tầng 2\"."),
      elevationMm: z.number().min(-100000).max(1000000).describe("Elevation in millimeters."),
      cadFile: z.string().min(1).max(1024).regex(/\.dwg$/i).optional()
        .describe("Full path of the storey's structural plan DWG. Omit to create only the level and plan."),
      mode: z.enum(["import", "link"]).optional().describe("Import (default) or link the DWG."),
    })).min(1).max(100).describe("Storeys to set up."),
  },
  annotations: propose,
  timeoutMs: 60000,
});

export const proposeGrids = defineTool({
  name: "propose_grids",
  method: "propose_grids",
  title: "Propose grids from CAD",
  description:
    "Plan grids read from one CAD drawing, without changing the model. Uses the CAD layer whose name contains GRID,"
    + " TRUC/TRỤC or AXIS; vertical axes are named 1, 2, 3... and horizontal ones A, B, C... from the bottom left."
    + " Grids are placed so grid 1-A sits on the Project Base Point, and the plan also moves this CAD (and every CAD"
    + " in alignCadIds, usually the other storeys' CAD from propose_levels) by the same vector so drawings stay on"
    + " the grids. One review item per grid and per CAD move; grids whose name already exists are Blocked."
    + " Opens the review window in Revit and returns a planId at once; then get_plan_status and commit_plan.",
  inputSchema: {
    cadId: elementId.describe("CAD import or link with the grid layer, from list_cad_sources."),
    viewId,
    alignCadIds: z.array(elementId).max(200).optional()
      .describe("Other CAD imports or links to move with it, e.g. every storey's CAD."),
  },
  annotations: propose,
  timeoutMs: 120000,
});

export const scanCadWalls = defineTool({
  name: "scan_cad_walls",
  method: "scan_cad_walls",
  title: "Scan CAD for walls",
  description:
    "Read structural walls from a CAD drawing as pairs of parallel lines (30-500 mm apart) as shown in a plan view,"
    + " with names from nearby Text Notes. Call first without layers to list the layers, ask the user which hold walls,"
    + " then call again with layers. Walls are grouped by tag name and thickness (key like \"V1|200\", or \"w200\""
    + " without a name), with type suggestions and a scanId for propose_walls. Changes nothing in the model.",
  inputSchema: {
    cadId: elementId.describe("CAD import or link ID from list_cad_sources."),
    viewId,
    layers: layerList("wall outlines"),
    region: regionMm.optional(),
    textSearchMm: z.number().min(1).max(20000).optional()
      .describe("How far from a wall to look for its text."),
  },
  annotations: readOnly,
  timeoutMs: 120000,
});

export const proposeWalls = defineTool({
  name: "propose_walls",
  method: "propose_walls",
  title: "Propose walls from scan",
  description:
    "Plan structural walls from a scan between two levels, without changing the model. One review item per wall group;"
    + " parts of centerlines already covered by a wall are skipped. Groups with a tag name are proposed unless listed"
    + " in 'exclude'; groups without one only when listed in 'include'. Type per group: 'types' entry, else a Basic"
    + " Wall type of that thickness, else a new type \"BTC-VachKC_<b>\" duplicated at commit from templateTypeId"
    + " (default: the Basic Wall type closest in thickness). Opens the review window and returns a planId at once;"
    + " then get_plan_status and commit_plan.",
  inputSchema: {
    scanId: z.string().min(1).max(64).describe("scanId from scan_cad_walls."),
    baseLevelId: elementId.describe("Level at the bottom of the walls."),
    topLevelId: elementId.describe("Level at the top of the walls."),
    baseOffsetMm: z.number().min(-100000).max(100000).optional().describe("Offset from the base level (default 0)."),
    topOffsetMm: z.number().min(-100000).max(100000).optional().describe("Offset from the top level (default 0)."),
    include: z.array(z.string().min(1).max(200)).max(500).optional()
      .describe("Keys of groups without a tag name to propose as well."),
    exclude: z.array(z.string().min(1).max(200)).max(500).optional()
      .describe("Keys of groups not to propose."),
    types: typeMap("V1|200"),
    templateTypeId: elementId.optional()
      .describe("Basic Wall type to duplicate for groups with no type of their thickness."),
    onlySupported: z.boolean().optional()
      .describe("Keep only walls with a wall or foundation within 500 mm below. Default false."),
    extendToColumns: z.boolean().optional()
      .describe("Extend wall ends to the nearest structural columns at the base level. Default false."),
    columnSearchMm: z.number().min(1).max(20000).optional()
      .describe("How far to look for a column from a wall end (default 3000)."),
    tagParameterName: z.string().min(1).max(200).optional()
      .describe("Text parameter of the wall that receives the group's tag name, e.g. \"Mark\"."),
  },
  annotations: propose,
  timeoutMs: 120000,
});

export const proposeFloors = defineTool({
  name: "propose_floors",
  method: "propose_floors",
  title: "Propose floors from beams and walls",
  description:
    "Plan structural floors on a level from the model, without changing it: centerlines of beams on the level and of"
    + " straight walls crossing it (as shown in a plan view) are traced into closed panels; panels under 1 m² are"
    + " treated as openings. One review item per panel. Floor type: floorTypeId, else BTC_SanKC_150 (150 mm), created"
    + " at commit if missing. Panels overlapping an existing floor are skipped at commit. Place beams and walls first."
    + " Opens the review window and returns a planId at once; then get_plan_status and commit_plan.",
  inputSchema: {
    levelId: elementId.describe("Level of the floors and of the beams that bound them."),
    viewId: elementId.optional()
      .describe("Plan view of the level. Defaults to the active view if it shows this level, else a plan of the level."),
    floorTypeId: elementId.optional().describe("Floor type to use. Default BTC_SanKC_150."),
    offsetMm: z.number().min(-100000).max(100000).optional().describe("Height offset from the level (default 0)."),
  },
  annotations: propose,
  timeoutMs: 120000,
});

export const scanCadColumns = defineTool({
  name: "scan_cad_columns",
  method: "scan_cad_columns",
  title: "Scan CAD for columns",
  description:
    "Read rectangular column outlines from a CAD drawing as shown in a plan view."
    + " Call first without layers to see which layers contain outlines and ask the user which are columns."
    + " Call again with layers to group columns by size (key like \"500x300\" in mm), get type suggestions"
    + " (exactType or nearestType), and a scanId for place_columns. Changes nothing in the model.",
  inputSchema: {
    cadId: elementId.describe("CAD import or link ID from list_cad_sources."),
    viewId,
    layers: layerList("column outlines"),
    region: regionMm.optional(),
  },
  annotations: readOnly,
  timeoutMs: 120000,
});

export const loadDefaultFamily = defineTool({
  name: "load_default_family",
  method: "load_default_family",
  title: "Load default family",
  description:
    "Load a company structural family packaged with the add-in: column BTC-CotCN_BeTong (b/h), beam BTC-DamCN_BeTong"
    + " (b/h), or pile BTC-CocTron_BeTong (Diameter). For piles it also creates the default cap (BTC-DaiMong_1500)"
    + " and lean-concrete (BTC-BeTongLot_100) Foundation Slab types when missing. Use when a scan reports no suitable"
    + " type; its familyId is the templateFamilyId for place_columns/place_beams. Does nothing if already loaded."
    + " Confirm with the user first.",
  inputSchema: {
    kind: z.enum(["column", "beam", "pile"]).describe("Which family to load."),
  },
  annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true },
  timeoutMs: 60000,
});

export const placeColumns = defineTool({
  name: "place_columns",
  method: "place_columns",
  title: "Place columns from scan",
  description:
    "Place structural columns from a scan between two levels, rotated as in the CAD; positions that already"
    + " have a column of the same family are skipped."
    + " dryRun defaults to true and only reports the plan: always show it to the user and get confirmation"
    + " before calling again with dryRun=false. A real run is one undo step in Revit and returns a batchId"
    + " for undo_batch. Each size group uses: its entry in 'types', else an existing type with matching b/h,"
    + " else a new type created in templateFamilyId.",
  inputSchema: {
    scanId: z.string().min(1).max(64).describe("scanId from scan_cad_columns."),
    baseLevelId: elementId.describe("Bottom level of the columns."),
    topLevelId: elementId.describe("Top level of the columns; must be higher than the base level."),
    types: typeMap("500x300"),
    templateFamilyId: elementId.optional()
      .describe("Column family (familyId from a scan type or load_default_family) in which to create types for groups with no matching type."),
    dryRun,
  },
  annotations: write,
  timeoutMs: 120000,
});

export const scanCadBeams = defineTool({
  name: "scan_cad_beams",
  method: "scan_cad_beams",
  title: "Scan CAD for beams",
  description:
    "Read beams from a CAD drawing as pairs of parallel lines, and their names/sizes from nearby Text Notes in the"
    + " view (e.g. \"D2-X7(220x600)\"). Call first without layers to list the layers, ask the user which hold beams,"
    + " then call again with layers. Groups: by tag name (placed by default), by bxh from bare text, or by width"
    + " only (key like \"b220\"). Optionally extends beam ends to columns on levelId. Changes nothing in the model.",
  inputSchema: {
    cadId: elementId.describe("CAD import or link ID from list_cad_sources."),
    viewId,
    layers: layerList("beam outlines"),
    region: regionMm.optional(),
    textSearchMm: z.number().min(1).max(20000).optional()
      .describe("How far from a beam to look for its text (default 1000)."),
    extendToColumns: z.boolean().optional()
      .describe("Extend beam ends to the nearest columns (needs levelId). Default false."),
    levelId: elementId.optional().describe("Level whose columns beams are extended to."),
    columnSearchMm: z.number().min(1).max(20000).optional()
      .describe("How far to look for a column from a beam end (default 3000)."),
  },
  annotations: readOnly,
  timeoutMs: 120000,
});

export const placeBeams = defineTool({
  name: "place_beams",
  method: "place_beams",
  title: "Place beams from scan",
  description:
    "Place structural beams from a scan on a level (offset 0); beams overlapping an existing beam are skipped."
    + " Groups with a tag name are placed unless listed in 'exclude'; groups without one only when listed in"
    + " 'include'. dryRun defaults to true and only reports the plan: always show it to the user and get"
    + " confirmation before calling again with dryRun=false. A real run is one undo step and returns a batchId."
    + " Type per group: 'types' entry, else an existing type with matching bxh, else a new type in templateFamilyId."
    + " Width-only groups always need a 'types' entry.",
  inputSchema: {
    scanId: z.string().min(1).max(64).describe("scanId from scan_cad_beams."),
    levelId: elementId.describe("Level the beams are placed on."),
    include: z.array(z.string().min(1).max(200)).max(500).optional()
      .describe("Keys of groups without a tag name to place as well."),
    exclude: z.array(z.string().min(1).max(200)).max(500).optional()
      .describe("Keys of groups not to place."),
    types: typeMap("D2-X7"),
    templateFamilyId: elementId.optional()
      .describe("Beam family (familyId from a scan type or load_default_family) in which to create bxh types."),
    tagParameterName: z.string().min(1).max(200).optional()
      .describe("Text parameter of the beam that receives the group's tag name, e.g. \"Mark\"."),
    dryRun,
  },
  annotations: write,
  timeoutMs: 120000,
});

export const scanCadPiles = defineTool({
  name: "scan_cad_piles",
  method: "scan_cad_piles",
  title: "Scan CAD for piles and caps",
  description:
    "Read pile circles and pile cap outlines from a CAD drawing as shown in a plan view. Call first without layers"
    + " to list layers with their circle and outline counts and ask the user which are piles and which are caps;"
    + " then call again with pileLayers and capLayers. Piles are grouped by diameter (key like \"D600\") and"
    + " assigned to the cap containing them. Changes nothing in the model.",
  inputSchema: {
    cadId: elementId.describe("CAD import or link ID from list_cad_sources."),
    viewId,
    pileLayers: layerList("pile circles"),
    capLayers: layerList("pile cap outlines"),
    region: regionMm.optional(),
  },
  annotations: readOnly,
  timeoutMs: 120000,
});

export const placePiles = defineTool({
  name: "place_piles",
  method: "place_piles",
  title: "Place piles and caps from scan",
  description:
    "Place piles and pile caps from a scan, like the BIMTECH pile tool: caps are Foundation Slabs on capLevelId"
    + " (default type BTC-DaiMong_1500), pile tops sit at cap level minus cap thickness, then lean concrete under"
    + " each cap (default BTC-BeTongLot_100, withLot=false to skip), dimensions rounded to the grid, and piles"
    + " joined with caps. dryRun defaults to true and only reports the plan: always show it to the user and get"
    + " confirmation before calling again with dryRun=false. A real run is one undo step and returns a batchId.",
  inputSchema: {
    scanId: z.string().min(1).max(64).describe("scanId from scan_cad_piles."),
    capLevelId: elementId.describe("Level at the top of the pile caps."),
    capFloorTypeId: elementId.optional().describe("Foundation Slab type for caps. Default BTC-DaiMong_1500."),
    withLot: z.boolean().optional().describe("Place lean concrete under caps. Default true."),
    lotFloorTypeId: elementId.optional().describe("Foundation Slab type for lean concrete. Default BTC-BeTongLot_100."),
    embedmentMm: z.number().min(0).max(5000).optional().describe("Pile embedment into the cap (default 100)."),
    lotOffsetMm: z.number().min(0).max(5000).optional().describe("Lean concrete extends this far beyond the cap (default 50)."),
    types: typeMap("D600"),
    createMissingTypes: z.boolean().optional()
      .describe("Create a pile type for each diameter without one, duplicated from the nearest type. Default false."),
    dryRun,
  },
  annotations: write,
  timeoutMs: 120000,
});

export const undoBatch = defineTool({
  name: "undo_batch",
  method: "undo_batch",
  title: "Undo a batch",
  description:
    "Delete the elements created by one write call, identified by its batchId. Types created by that call"
    + " are deleted only if nothing uses them. Confirm with the user first.",
  inputSchema: {
    batchId: z.string().min(1).max(64).describe("batchId returned by a write tool such as place_columns."),
  },
  annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false },
  timeoutMs: 60000,
});
