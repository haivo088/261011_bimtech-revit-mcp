import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { config } from "./config.js";
import { registerTools } from "./tools/index.js";

async function main(): Promise<void> {
  const server = new McpServer({ name: "bimtech-revit-mcp", version: "0.4.0" });
  const availability = registerTools(server);
  await server.connect(new StdioServerTransport());
  // Hide tools switched off in Revit's settings; keep checking because the user may change them anytime.
  if (config.availabilityPollMs > 0) {
    void availability.sync();
    availability.startPolling(config.availabilityPollMs);
  }
  // stdout belongs to MCP. Human-readable diagnostics go to stderr.
  console.error("BIMTECH Revit MCP started.");
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
