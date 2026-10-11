// Read connection settings once when the MCP server starts.
function positiveInteger(name: string, fallback: number, maximum: number): number {
  const raw = process.env[name];
  if (raw === undefined) return fallback;
  const value = Number(raw);
  if (!Number.isInteger(value) || value < 1 || value > maximum) {
    throw new Error(`${name} must be an integer between 1 and ${maximum}.`);
  }
  return value;
}

export const config = {
  host: process.env.REVIT_MCP_HOST ?? "127.0.0.1",
  port: positiveInteger("REVIT_MCP_PORT", 8081, 65535),
  timeoutMs: positiveInteger("REVIT_MCP_TIMEOUT_MS", 15000, 120000),
};
