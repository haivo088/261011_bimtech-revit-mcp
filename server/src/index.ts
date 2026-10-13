import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { registerTools } from "./tools/index.js";

async function main(): Promise<void> {
  const server = new McpServer({ name: "bimtech-revit-mcp", version: "0.2.0" });
  registerTools(server);
  await server.connect(new StdioServerTransport());
  // stdout belongs to MCP. Human-readable diagnostics go to stderr.
  console.error("BIMTECH Revit MCP started.");
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
