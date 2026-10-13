import { z } from "zod";

// Input fields shared by many tools. The add-in reads the same names in
// addin/Commands/Common (PageRequest, ElementQuery) and validates them again.

export const elementId = z
  .string()
  .regex(/^-?\d+$/, "Element IDs are digit strings, e.g. \"12345\".")
  .describe("Revit element ID as a string, e.g. \"12345\".");

export const paging = {
  limit: z.number().int().min(1).max(500).optional()
    .describe("Maximum items to return (default 50, max 500)."),
  offset: z.number().int().min(0).optional()
    .describe("Items to skip. Pass nextOffset from the previous page to continue."),
};

export const elementFilter = {
  categories: z.array(z.string().min(1).max(100)).max(50).optional()
    .describe("Built-in category names such as \"OST_Walls\" or \"Doors\". Call list_categories to see what the model contains."),
  viewId: elementId.optional().describe("Only elements visible in this view."),
  levelId: elementId.optional().describe("Only elements on this level. Call get_levels for IDs."),
  nameContains: z.string().min(1).max(200).optional()
    .describe("Case-insensitive text the element name must contain."),
  includeTypes: z.boolean().optional()
    .describe("List element types (family types) instead of placed instances. Default false."),
};

// Appended to descriptions of tools that return pages.
export const pagingNote =
  " Results are paged: check hasMore and call again with offset = nextOffset to get more.";

// Appended to descriptions of tools that return measured values.
export const unitsNote =
  " Values use fixed units regardless of project settings: lengths mm, areas m2, volumes m3, angles degrees;"
  + " 'display' shows Revit's formatted text.";
