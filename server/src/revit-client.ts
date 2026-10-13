import { randomUUID } from "node:crypto";
import { createConnection } from "node:net";
import { config } from "./config.js";

export type RevitParams = Record<string, unknown>;

export interface CallOptions {
  host: string;
  port: number;
  // How long the add-in may spend on this request, including waiting in Revit's queue.
  timeoutMs: number;
  // Extra time the client waits so the add-in's own timeout error can arrive first.
  graceMs: number;
}

const defaultOptions: CallOptions = {
  host: config.host,
  port: config.port,
  timeoutMs: config.timeoutMs,
  graceMs: 2000,
};

const maxResponseBytes = 1024 * 1024;

// An error reported by the add-in as a JSON-RPC error object.
export class RevitError extends Error {
  constructor(readonly code: number, message: string, readonly data?: unknown) {
    super(`Revit error ${code}: ${message}`);
    this.name = "RevitError";
  }
}

// One command per connection keeps the first implementation easy to follow.
// A newline ends each JSON message; TCP chunks are not message boundaries.
export function callRevit(
  method: string,
  params: RevitParams = {},
  overrides: Partial<CallOptions> = {},
): Promise<unknown> {
  const options = { ...defaultOptions, ...overrides };
  return new Promise((resolve, reject) => {
    const id = randomUUID();
    const waitMs = options.timeoutMs + options.graceMs;
    const socket = createConnection({ host: options.host, port: options.port });
    socket.setEncoding("utf8"); // Preserve Unicode even when bytes arrive separately.
    let buffer = "";
    let finished = false;
    const timer = setTimeout(() => finish(new Error(
      `Revit did not respond within ${waitMs} ms. Check the BIMTECH bridge and close any modal dialogs.`,
    )), waitMs);

    function finish(error?: Error, result?: unknown): void {
      if (finished) return;
      finished = true;
      clearTimeout(timer);
      socket.destroy();
      if (error) reject(error);
      else resolve(result);
    }

    socket.once("connect", () => {
      const request = { jsonrpc: "2.0", id, method, params, timeoutMs: options.timeoutMs };
      socket.write(JSON.stringify(request) + "\n");
    });

    socket.on("data", (chunk: string) => {
      buffer += chunk;
      if (Buffer.byteLength(buffer, "utf8") > maxResponseBytes) {
        finish(new Error("Revit response exceeds the 1 MiB limit. Narrow the request or use paging."));
        return;
      }
      const newline = buffer.indexOf("\n");
      if (newline === -1) return;
      try {
        const response = JSON.parse(buffer.slice(0, newline));
        if (response.jsonrpc !== "2.0" || response.id !== id) {
          throw new Error("Invalid JSON-RPC response or mismatched request ID.");
        }
        if (response.error) {
          throw new RevitError(response.error.code, response.error.message, response.error.data);
        }
        if (!("result" in response)) throw new Error("Revit response has no result.");
        finish(undefined, response.result);
      } catch (error) {
        finish(error instanceof Error ? error : new Error(String(error)));
      }
    });

    socket.once("error", (error) => finish(new Error(
      `Cannot communicate with Revit at ${options.host}:${options.port}: ${error.message}`,
    )));
    socket.once("end", () => finish(new Error("Revit closed the connection before sending a complete response.")));
    socket.once("close", () => finish(new Error("Revit connection closed before a response arrived.")));
  });
}
