import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit } from "./revit-client.js";

// MCP tool names are public; method names are the internal C# contract.
export function registerTools(server: McpServer): void {
  const tools = [
    {
      name: "ping_revit",
      method: "ping",
      description: "Check the BIMTECH Revit bridge and report the Revit version.",
    },
    {
      name: "get_document_info",
      method: "get_document_info",
      description: "Read the title, path, type, and active view of the open Revit model.",
    },
  ];

  for (const tool of tools) {
    server.registerTool(tool.name, {
      description: tool.description,
      inputSchema: {},
      annotations: { readOnlyHint: true, destructiveHint: false },
    }, async () => {
      try {
        const result = await callRevit(tool.method);
        return { content: [{ type: "text", text: JSON.stringify(result, null, 2) }] };
      } catch (error) {
        return {
          isError: true,
          content: [{ type: "text", text: error instanceof Error ? error.message : String(error) }],
        };
      }
    });
  }
}
