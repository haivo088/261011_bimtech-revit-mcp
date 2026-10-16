import { z } from "zod";
import { defineTool, readOnly } from "./define.js";
import { elementId } from "./schemas.js";

// CAD-to-structure workflow. Each element kind follows the same steps:
//   list_cad_sources → scan_cad_<kind> (layers) → scan_cad_<kind> (groups, scanId) → propose_<kind>
//   → the user reviews the plan in Revit → commit_plan. Beams need no scan: propose_beams reads the CAD itself.
// Geometry stays in the add-in; tools exchange scanId/planId and summaries only.

const regionMm = z.object({
  minX: z.number(), minY: z.number(), maxX: z.number(), maxY: z.number(),
}).describe("Optional plan rectangle in millimeters (model coordinates); only outlines whose center lies inside are kept.");

const viewId = elementId.optional()
  .describe("Plan view showing the CAD. Defaults to the CAD's own view if it is view-specific, else the active view.");
const layerList = (what: string) => z.array(z.string().min(1).max(255)).max(50).optional()
  .describe(`CAD layer names holding ${what}, exactly as returned by the first call.`);
const typeMap = (example: string) => z.record(z.string(), elementId).optional()
  .describe(`Type to use per group key, e.g. {"${example}": "12345"}. Overrides automatic matching.`);

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

export const suggestLevelsFromFolder = defineTool({
  name: "suggest_levels_from_folder",
  method: "suggest_levels_from_folder",
  title: "Suggest storeys from DWG file names",
  description:
    "Read the DWG file names in a folder and propose the storey table for propose_levels: storey name and elevation"
    + " read from names like \"KC-03 MB TANG 2 (+3.600).dwg\" (Tầng N, Hầm N, Móng, Tầng trệt, Tầng lửng, Mái, Tum;"
    + " elevations in metres such as +3.600, -1,500, COS 7.20), bottom to top. Sections, details and rebar layouts are"
    + " listed apart. Nothing is guessed: missing names or elevations stay null, and warnings flag storeys with several"
    + " files or unusual storey heights. Confirm the table with the user before propose_levels. Changes nothing.",
  inputSchema: {
    folder: z.string().min(3).max(1024).describe("Absolute path of the folder with the structural plan DWGs."),
    recursive: z.boolean().optional().describe("Also look in subfolders (default false)."),
  },
  annotations: readOnly,
});

export const proposeLevels = defineTool({
  name: "propose_levels",
  method: "propose_levels",
  title: "Propose levels with their CAD",
  description:
    "Plan setting up storeys, without changing the model. Each storey becomes one review item that creates the level,"
    + " reuses one with the same name and elevation, or renames the one level of another name at that elevation (such"
    + " as the template's \"Level 1\") instead of duplicating it; then its structural plan \"KC - <name>\" and imports or"
    + " links that storey's DWG into the plan at the origin. Problems (forbidden characters, missing file, level at"
    + " another elevation, two storeys at one elevation) become Blocked items; unusual storey heights are flagged."
    + " Levels nobody uses get a delete item that starts rejected. Use suggest_levels_from_folder to build the table"
    + " from DWG file names. Opens the review window in Revit and returns a planId at once; ask the user to review in"
    + " Revit, then get_plan_status and commit_plan. After the commit check each item's cadWarning (wrong DWG unit),"
    + " then list_cad_sources and propose_grids to build grids and align the CAD.",
  inputSchema: {
    levels: z.array(z.object({
      name: z.string().min(1).max(255).describe("Level name, e.g. \"Tầng 2\"."),
      elevationMm: z.number().min(-100000).max(1000000).describe("Elevation in millimeters."),
      cadFile: z.string().min(1).max(1024).regex(/\.dwg$/i).optional()
        .describe("Full path of the storey's structural plan DWG. Omit to create only the level and plan."),
      mode: z.enum(["import", "link"]).optional().describe("Import (default) or link the DWG."),
      cadUnit: z.enum(["mm", "cm", "m", "auto"]).optional()
        .describe("Unit the DWG is drawn in (default mm); auto reads it from the file."),
    })).min(1).max(100).describe("Storeys to set up."),
    viewTemplate: z.string().min(1).max(255).optional()
      .describe("Name of a structural plan view template to apply to the new plans."),
    scale: z.number().int().min(1).max(10000).optional().describe("Scale of the new plans, e.g. 100 for 1:100."),
    proposeLevelCleanup: z.boolean().optional()
      .describe("Offer to delete levels nobody uses, as items that start rejected (default true)."),
  },
  annotations: propose,
  timeoutMs: 60000,
});

export const proposeGrids = defineTool({
  name: "propose_grids",
  method: "propose_grids",
  title: "Propose grids from CAD",
  description:
    "Plan grids read from one CAD drawing, without changing the model. Grid lines are found by their line pattern"
    + " (center / dash-dot) first, then circles with a name at their ends (grid bubbles), then a layer name with GRID,"
    + " TRUC/TRỤC or AXIS; or pass gridLayer. Names come from the bubbles when their text is readable as Text Notes,"
    + " else 1, 2, 3... (vertical, left to right) and A, B, C... (horizontal, bottom up). Each bay is snapped to"
    + " snapModuleMm when within snapToleranceMm (5998.7 → 6000) and grids are laid out from the first axis, which"
    + " sits on the Project Base Point. This CAD and every CAD in alignCadIds (usually the other storeys' CAD) is moved"
    + " by its own vector, found by matching its axes to this grid; where that fails it uses this CAD's vector and says"
    + " so. Skewed grids are reported, not built. Grids and moved CAD are pinned unless pin is false. One review item"
    + " per grid and per CAD move; names already in the model are Blocked. Opens the review window in Revit and returns"
    + " a planId at once; then get_plan_status and commit_plan.",
  inputSchema: {
    cadId: elementId.describe("CAD import or link with the grid lines, from list_cad_sources."),
    viewId,
    alignCadIds: z.array(elementId).max(200).optional()
      .describe("Other CAD imports or links to move onto the grid, e.g. every storey's CAD."),
    gridLayer: z.string().min(1).max(255).optional()
      .describe("CAD layer holding the grid lines, when automatic detection picks the wrong one."),
    snapModuleMm: z.number().min(0).max(1000).optional()
      .describe("Bays are snapped to multiples of this (default 50). 0 only rounds to whole mm."),
    snapToleranceMm: z.number().min(0).max(100).optional()
      .describe("Largest drawing error snapped away (default 10); bays further off keep their measured size."),
    pin: z.boolean().optional().describe("Pin the new grids and moved CAD (default true)."),
  },
  annotations: propose,
  timeoutMs: 120000,
});

export const scanCadWalls = defineTool({
  name: "scan_cad_walls",
  method: "scan_cad_walls",
  title: "Scan CAD for walls",
  description:
    "Read structural walls from a CAD drawing. Normally (no layers): from the hatches of the DWG file itself, read from"
    + " disk (a linked CAD's own file, or dwgPath for an imported one) and placed on the CAD by matching lines. Each"
    + " hatched strip is weighed like an engineer would: hatch pattern shared with the columns, concrete thickness, on a"
    + " grid, joined to a modeled column, on a wall of the storey below, part of a core (with lift text); masonry"
    + " thickness, flat-column proportions or a column/masonry layer name count against. Column-sized hatches are never"
    + " walls. Walls are grouped by thickness: \"w300\" (enough evidence, proposed by default) and \"w300-maybe\" (to"
    + " review, only when included), each with its evidence counts, type suggestions and a scanId for propose_walls."
    + " Model the grids and columns first: the region defaults to the grids' extent and they add evidence. With"
    + " layers: the older reading of parallel line pairs on those layers, for drawings without hatches. Reading a"
    + " large DWG takes up to a minute the first time; repeated scans reuse it. Changes nothing in the model.",
  inputSchema: {
    cadId: elementId.describe("CAD import or link ID from list_cad_sources."),
    viewId,
    levelId: elementId.optional().describe("Base level of the walls. Default: the plan view's level."),
    dwgPath: z.string().min(1).max(1024).optional()
      .describe("Absolute path of the DWG/DXF file of an imported (not linked) CAD. A linked CAD's own file is used by default."),
    region: regionMm.optional(),
    gridToleranceMm: z.number().min(0).max(200).optional().describe("How close to a grid a wall face or centre counts as on it (default 15)."),
    columnMaxLengthMm: z.number().min(0).max(5000).optional()
      .describe("Hatches no longer than this are columns, not walls (default 1000)."),
    sureScore: z.number().int().min(1).max(20).optional().describe("Evidence score from which a wall is proposed by default (default 3)."),
    layers: layerList("wall outlines").describe("Only for drawings without hatches: layers holding wall outlines as line pairs."),
    textSearchMm: z.number().min(1).max(20000).optional()
      .describe("With layers only: how far from a wall to look for its text."),
  },
  annotations: readOnly,
  timeoutMs: 300000,
});

export const proposeWalls = defineTool({
  name: "propose_walls",
  method: "propose_walls",
  title: "Propose walls from scan",
  description:
    "Plan structural walls from a scan between two levels, without changing the model. One review item per wall group;"
    + " parts of centerlines already covered by a wall are skipped. Groups proposed by default (\"wNNN\" from"
    + " hatches, or with a tag name) are proposed unless listed in 'exclude'; the others only when listed in 'include'. Type per group: 'types' entry, else a Basic"
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
      .describe("Keys of groups not proposed by default to propose as well (\"w300-maybe\", or groups without a tag name)."),
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
    "Read rectangular column outlines (100-2000 mm, possibly rotated) from a CAD drawing as shown in a plan view,"
    + " drawn as polylines or loose lines. Call first without layers to see which layers contain outlines and ask the"
    + " user which are columns. Call again with layers to group columns by mark and size: each column's mark is read"
    + " from the nearest Text Note (\"C1 300x500\"), key \"C1\", or the size b×h small side first (\"300x500\") when"
    + " unmarked. Shapes of column size that are not rectangles (L-shaped columns, columns drawn touching a wall) are"
    + " listed in notRectangles and never placed; tell the user. Returns type suggestions (exactType or nearestType),"
    + " mark/size mismatches, and a scanId for propose_columns. Round columns are not supported. Changes nothing.",
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
    + " type; its familyId is the templateFamilyId for propose_columns/propose_beams. Does nothing if already loaded."
    + " Confirm with the user first.",
  inputSchema: {
    kind: z.enum(["column", "beam", "pile"]).describe("Which family to load."),
  },
  annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true },
  timeoutMs: 60000,
});

export const proposeColumns = defineTool({
  name: "propose_columns",
  method: "propose_columns",
  title: "Propose columns from scan",
  description:
    "Plan structural columns from a scan between two levels, without changing the model. One review item per group"
    + " (mark and size). Columns usually stand at grid crossings: per direction, a column drawn within gridToleranceMm"
    + " (default 15) of centred on a grid or of its face on the grid is set exactly there, other offsets are rounded to"
    + " snapMm (default 5). Items name the crossings (\"A-1\"); columns on one grid only or on none are flagged; grid"
    + " crossings with no column are returned in emptyCrossings for the user to check. A column drawn within 2° of the"
    + " grid direction is set straight; columns drawn twice are placed once; columns already in the model at that spot"
    + " (any family) are skipped. Type per group: its entry in 'types', else an existing type with matching b/h, else a b×h type"
    + " created in templateFamilyId. The mark goes to the Mark parameter (writeMark, default true). Opens the review"
    + " window and returns a planId at once; then get_plan_status and commit_plan.",
  inputSchema: {
    scanId: z.string().min(1).max(64).describe("scanId from scan_cad_columns."),
    baseLevelId: elementId.describe("Bottom level of the columns."),
    topLevelId: elementId.describe("Top level of the columns; must be higher than the base level."),
    types: typeMap("C1"),
    templateFamilyId: elementId.optional()
      .describe("Column family (familyId from a scan type or load_default_family) in which to create types for groups with no matching type."),
    snapMm: z.number().min(0).max(100).optional()
      .describe("Round each column's offset from the nearest grid to this (default 5); 0 keeps the drawing's position."),
    gridToleranceMm: z.number().min(0).max(100).optional()
      .describe("A column drawn this close to centred on a grid, or to its face on the grid, is set exactly there (default 15)."),
    gridMarginMm: z.number().min(0).max(5000).optional()
      .describe("A grid counts as passing through a column when within this of its faces (default 300)."),
    writeMark: z.boolean().optional().describe("Write the column mark to the Mark parameter (default true)."),
  },
  annotations: propose,
  timeoutMs: 120000,
});

export const proposeBeams = defineTool({
  name: "propose_beams",
  method: "propose_beams",
  title: "Propose main beams from CAD",
  description:
    "Plan the main beams of one level from a CAD plan, without changing the model. Model the columns (and walls) below"
    + " the level first: they are the supports. A beam is a pair of parallel CAD lines leaving a column (100-1000 mm"
    + " apart, middle within the column) and reaching the next column or wall in line, across gaps where other beams"
    + " cross. Beams run centre to centre of their supports and split only at supports, not at grid crossings. Marks"
    + " like \"D1 (220x600)\" are read from Text Notes in the view (the nearest beam parallel to the text, then spread"
    + " along the beams in line); b comes from the mark, else from the lines; h only from the mark or heightsMm, else"
    + " the group is Blocked: ask the user. The offset from the grid is set like columns (centred or face on the grid"
    + " within gridToleranceMm, else rounded to snapMm). One review item per group (mark and size); beams already in"
    + " the model are skipped. Reports dangling edges (secondary beams or consoles, not placed yet) and unused marks."
    + " Opens the review window and returns a planId at once; then get_plan_status and commit_plan.",
  inputSchema: {
    cadId: elementId.describe("CAD import or link ID from list_cad_sources."),
    viewId,
    levelId: elementId.describe("Level the beams are placed on (top of beam at the level plus offsetMm)."),
    offsetMm: z.number().min(-100000).max(100000).optional().describe("Top of beam above the level (default 0)."),
    layers: layerList("beam edges").describe("Optional: only lines on these CAD layers. Default: every layer."),
    types: typeMap("D1"),
    heightsMm: z.record(z.string(), z.number().min(100).max(5000)).optional()
      .describe("Beam height h per group key for groups whose mark gives no size, e.g. {\"b220\": 500}. Ask the user."),
    templateFamilyId: elementId.optional()
      .describe("Beam family in which to create b×h types. Default: BTC-DamCN_BeTong when loaded (load_default_family kind=beam)."),
    writeMark: z.boolean().optional().describe("Write the beam mark to the Mark parameter (default true)."),
    snapMm: z.number().min(0).max(100).optional()
      .describe("Round each beam's offset from the nearest grid to this (default 5); 0 keeps the drawing's position."),
    gridToleranceMm: z.number().min(0).max(100).optional()
      .describe("A beam drawn this close to centred on a grid, or to its face on the grid, is set exactly there (default 15)."),
    gridMarginMm: z.number().min(0).max(5000).optional()
      .describe("A grid counts as passing through a beam when within this of its faces (default 300)."),
    minWidthMm: z.number().min(50).max(2000).optional().describe("Narrowest beam (default 100)."),
    maxWidthMm: z.number().min(50).max(3000).optional().describe("Widest beam (default 1000)."),
    probeMm: z.number().min(50).max(3000).optional()
      .describe("How far from a column face the beam edges must start (default 300)."),
    maxGapMm: z.number().min(0).max(10000).optional()
      .describe("Longest break in a beam edge, e.g. where another beam crosses (default 1500)."),
    minCoverage: z.number().min(0.1).max(1).optional()
      .describe("Share of the span the edges must draw (default 0.6)."),
    tagDistanceMm: z.number().min(0).max(10000).optional()
      .describe("How far from a beam its mark may be (default 1000)."),
  },
  annotations: propose,
  timeoutMs: 180000,
});

export const scanCadPiles = defineTool({
  name: "scan_cad_piles",
  method: "scan_cad_piles",
  title: "Scan CAD for piles and caps",
  description:
    "Read pile circles and pile cap outlines from a CAD drawing as shown in a plan view. Call first without layers"
    + " to list layers with their circle, closed outline and loose line counts and ask the user which are piles and"
    + " which are caps; then call again with pileLayers and capLayers. Caps drawn as loose lines are joined into loops."
    + " Piles are grouped by diameter (key like \"D600\") and assigned to the cap containing them; piles further than"
    + " 300 mm from every cap belong to none. Each cap's mark is read from a Text Note in or beside it, e.g."
    + " \"Đ1 (2000x2000x1200)\" gives its thickness; caps are grouped by mark, else by size (capGroups). Round piles only."
    + " Changes nothing in the model.",
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

export const proposePiles = defineTool({
  name: "propose_piles",
  method: "propose_piles",
  title: "Propose piles and caps from scan",
  description:
    "Plan piles and pile caps from a scan, without changing the model. One review item per cap: the cap (Foundation"
    + " Slab on capLevelId), its piles (top = cap level − that cap's thickness) and lean concrete under it (withLot)."
    + " Cap thickness per cap group (key from capGroups of the scan): capTypes entry, else capThicknessMm entry, else the"
    + " thickness in the cap mark, else capFloorTypeId for all caps; otherwise the cap is Blocked: ask the user, never"
    + " guess. A thickness with no type of it creates BTC-DaiMong_<h> at commit. Cap edges are squared to the grid and"
    + " rounded to snapMm (default 5), and caps and piles are shifted so their offsets from the grids are multiples of"
    + " snapMm. Piles join only their own cap and lean concrete. Piles in no cap get one Blocked item. Opens the review"
    + " window and returns a planId at once; then get_plan_status and commit_plan.",
  inputSchema: {
    scanId: z.string().min(1).max(64).describe("scanId from scan_cad_piles."),
    capLevelId: elementId.describe("Level at the top of the pile caps."),
    capTypes: typeMap("Đ1")
      .describe("Foundation Slab type per cap group key, e.g. {\"Đ1\": \"12345\", \"2000x2000\": \"12346\"}."),
    capThicknessMm: z.record(z.string(), z.number().min(100).max(5000)).optional()
      .describe("Cap thickness per cap group key in mm, e.g. {\"Đ1\": 1200}, when the drawing does not give it."),
    capFloorTypeId: elementId.optional().describe("Foundation Slab type for every cap without other information."),
    capTemplateTypeId: elementId.optional()
      .describe("Foundation Slab type duplicated to make cap types of a new thickness. Default BTC-DaiMong_1500."),
    withLot: z.boolean().optional().describe("Place lean concrete under caps. Default true."),
    lotFloorTypeId: elementId.optional().describe("Foundation Slab type for lean concrete. Default BTC-BeTongLot_100."),
    lotOffsetMm: z.number().min(0).max(5000).optional().describe("Lean concrete extends this far beyond the cap (default 50)."),
    embedmentMm: z.number().min(0).max(5000).optional().describe("Pile embedment into the cap (default 100)."),
    pileLengthMm: z.number().min(1000).max(150000).optional()
      .describe("Pile length, set on the pile's Length parameter when the family has one."),
    types: typeMap("D600"),
    createMissingTypes: z.boolean().optional()
      .describe("Create a pile type for each diameter without one, duplicated from the nearest type. Default false."),
    snapMm: z.number().min(0).max(100).optional()
      .describe("Round cap sizes and offsets from grids to this (default 5); 0 keeps the drawing's values."),
    join: z.boolean().optional().describe("Join each pile with its cap and lean concrete (default true)."),
  },
  annotations: propose,
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
    batchId: z.string().min(1).max(64).describe("batchId returned by a write tool such as load_default_family."),
  },
  annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false },
  timeoutMs: 60000,
});
