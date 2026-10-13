import { z } from "zod";
import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { callRevit, RevitError, type RevitParams } from "../revit-client.js";

type Shape = z.ZodRawShape;
type Input<S extends Shape> = z.infer<z.ZodObject<S>>;

// Every tool must state whether it changes the model. Claude and MCP clients
// use these hints to decide when to ask the user before calling a tool.
export interface RevitToolAnnotations {
  readOnlyHint: boolean;
  destructiveHint: boolean;
  idempotentHint?: boolean;
}

// One MCP tool backed by one add-in method.
export interface RevitTool<S extends Shape = Shape> {
  name: string; // Public name Claude sees, e.g. "get_document_info".
  method: string; // Internal C# contract, registered in addin/Commands/CommandRegistry.cs.
  title: string;
  description: string;
  inputSchema: S; // Validated by the MCP SDK before the handler runs.
  annotations: RevitToolAnnotations;
  timeoutMs?: number; // Overrides REVIT_MCP_TIMEOUT_MS for slow operations.
  // Converts validated input to add-in params. By default input is sent unchanged.
  toParams?: (input: Input<S>) => RevitParams;
}

// Keeps the input type of each tool when tools are collected in one array.
export function defineTool<S extends Shape>(tool: RevitTool<S>): RevitTool {
  return tool as unknown as RevitTool;
}

export function registerRevitTools(server: McpServer, tools: readonly RevitTool[]): void {
  const names = new Set<string>();
  for (const tool of tools) {
    if (names.has(tool.name)) throw new Error(`Duplicate MCP tool name: ${tool.name}`);
    names.add(tool.name);

    server.registerTool(tool.name, {
      title: tool.title,
      description: tool.description,
      inputSchema: tool.inputSchema,
      annotations: { ...tool.annotations, title: tool.title, openWorldHint: false },
    }, async (input: Input<Shape>) => {
      try {
        const params = tool.toParams ? tool.toParams(input) : input;
        const options = tool.timeoutMs === undefined ? {} : { timeoutMs: tool.timeoutMs };
        const result = await callRevit(tool.method, params, options);
        return textResult(JSON.stringify(result, null, 2));
      } catch (error) {
        return errorResult(error);
      }
    });
  }
}

function textResult(text: string, isError = false): CallToolResult {
  return { isError, content: [{ type: "text", text }] };
}

// Turns any failure into text Claude can read and act on.
function errorResult(error: unknown): CallToolResult {
  if (error instanceof RevitError && error.data !== undefined) {
    return textResult(`${error.message}\nDetails: ${JSON.stringify(error.data)}`, true);
  }
  return textResult(error instanceof Error ? error.message : String(error), true);
}
