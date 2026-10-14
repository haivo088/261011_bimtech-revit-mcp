import { z } from "zod";
import type { McpServer, RegisteredTool } from "@modelcontextprotocol/sdk/server/mcp.js";
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

// Annotations for tools that only read the model.
export const readOnly: RevitToolAnnotations = { readOnlyHint: true, destructiveHint: false, idempotentHint: true };

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

// Codes meaning the add-in's command list or settings differ from what Claude was shown.
const availabilityErrors = new Set([-32601, -32008]);

// Registers every tool and returns the SDK handles so tools can be hidden later.
// onAvailabilityError runs when Revit reports a command as unknown or switched off.
export function registerRevitTools(
  server: McpServer, tools: readonly RevitTool[], onAvailabilityError?: () => void,
): Map<RevitTool, RegisteredTool> {
  const registered = new Map<RevitTool, RegisteredTool>();
  for (const tool of tools) {
    if ([...registered.keys()].some((other) => other.name === tool.name)) {
      throw new Error(`Duplicate MCP tool name: ${tool.name}`);
    }

    const handle = server.registerTool(tool.name, {
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
        if (error instanceof RevitError && availabilityErrors.has(error.code)) onAvailabilityError?.();
        return errorResult(error);
      }
    });
    registered.set(tool, handle);
  }
  return registered;
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
