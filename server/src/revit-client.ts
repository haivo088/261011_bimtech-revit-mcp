import { randomUUID } from "node:crypto";
import { createConnection } from "node:net";
import { config } from "./config.js";

type ConnectionOptions = typeof config;

// One command per connection keeps the first implementation easy to follow.
// A newline ends each JSON message; TCP chunks are not message boundaries.
export function callRevit(
  method: string,
  options: ConnectionOptions = config,
): Promise<unknown> {
  return new Promise((resolve, reject) => {
    const id = randomUUID();
    const socket = createConnection({ host: options.host, port: options.port });
    socket.setEncoding("utf8"); // Preserve Unicode even when bytes arrive separately.
    let buffer = "";
    let finished = false;
    const timer = setTimeout(() => finish(new Error(
      `Revit did not respond within ${options.timeoutMs} ms. Check the BIMTECH bridge and close any modal dialogs.`,
    )), options.timeoutMs);

    function finish(error?: Error, result?: unknown): void {
      if (finished) return;
      finished = true;
      clearTimeout(timer);
      socket.destroy();
      if (error) reject(error);
      else resolve(result);
    }

    socket.once("connect", () => {
      const request = { jsonrpc: "2.0", id, method, params: {} };
      socket.write(JSON.stringify(request) + "\n");
    });

    socket.on("data", (chunk: string) => {
      buffer += chunk;
      if (Buffer.byteLength(buffer, "utf8") > 1024 * 1024) {
        finish(new Error("Revit response exceeds the 1 MiB limit."));
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
          throw new Error(`Revit error ${response.error.code}: ${response.error.message}`);
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
