import { readFileSync } from "node:fs";
import { join } from "node:path";

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

function nonNegativeInteger(name: string, fallback: number, maximum: number): number {
  return process.env[name] === "0" ? 0 : positiveInteger(name, fallback, maximum);
}

// The add-in's settings window writes this file; reading the port from it keeps both sides in step.
export const settingsFile = process.env.REVIT_MCP_SETTINGS
  ?? join(process.env.APPDATA ?? "", "BIMTECH", "RevitMcp", "settings.json");

function portFromSettingsFile(): number | undefined {
  try {
    const text = readFileSync(settingsFile, "utf8").replace(/^\uFEFF/, "");
    const port = JSON.parse(text).port;
    return Number.isInteger(port) && port >= 1024 && port <= 65535 ? port : undefined;
  } catch {
    return undefined; // Missing or unreadable file: use the default, like the add-in does.
  }
}

export const config = {
  host: process.env.REVIT_MCP_HOST ?? "127.0.0.1",
  // REVIT_MCP_PORT overrides the settings file, mainly for tests.
  port: positiveInteger("REVIT_MCP_PORT", portFromSettingsFile() ?? 8081, 65535),
  timeoutMs: positiveInteger("REVIT_MCP_TIMEOUT_MS", 15000, 120000),
  // How often to re-check which tools the user has switched off in Revit. 0 turns the check off
  // entirely, including the one at startup (used by tests with a scripted fake bridge).
  availabilityPollMs: nonNegativeInteger("REVIT_MCP_AVAILABILITY_POLL_MS", 30000, 3600000),
  // BIMTECH_DEV=1 adds the developer tools (server/src/tools/dev.ts). Only the developer sets it.
  devTools: process.env.BIMTECH_DEV === "1",
  // How often wait_for_plan asks Revit for the plan's status.
  waitPollMs: positiveInteger("REVIT_MCP_WAIT_POLL_MS", 2000, 60000),
};
