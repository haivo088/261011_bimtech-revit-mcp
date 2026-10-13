import { createServer } from "node:net";
import { once } from "node:events";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

// A small TCP stand-in verifies transport behavior without opening Revit.
export async function bridge(t, respond) {
  const sockets = new Set();
  const server = createServer((socket) => {
    sockets.add(socket);
    socket.on("close", () => sockets.delete(socket));
    socket.on("error", () => {});
    socket.setEncoding("utf8");
    let buffer = "";
    socket.on("data", (chunk) => {
      buffer += chunk;
      const newline = buffer.indexOf("\n");
      if (newline < 0) return;
      const request = JSON.parse(buffer.slice(0, newline));
      socket.removeAllListeners("data");
      Promise.resolve(respond(socket, request)).catch(() => socket.destroy());
    });
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  t.after(() => new Promise((resolve) => {
    for (const socket of sockets) socket.destroy();
    server.close(resolve);
  }));
  return { host: "127.0.0.1", port: server.address().port, timeoutMs: 2000, graceMs: 0 };
}

// Starts the built MCP server as Claude would and points it at the fake bridge.
// Availability sync is off unless a test passes its own env, so scripted bridges see only the tool calls.
export async function mcpClient(t, port, extraEnv = {}) {
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [fileURLToPath(new URL("../build/index.js", import.meta.url))],
    // A key set to undefined in extraEnv is removed, e.g. to test reading the port from settings.json.
    env: Object.fromEntries(Object.entries({
      ...process.env, REVIT_MCP_PORT: String(port), REVIT_MCP_AVAILABILITY_POLL_MS: "0", ...extraEnv,
    }).filter(([, value]) => value !== undefined)),
    stderr: "pipe",
  });
  // Show server diagnostics in the test log; stdout stays reserved for MCP.
  transport.stderr?.on("data", (chunk) => t.diagnostic(String(chunk).trim()));
  const client = new Client({ name: "bimtech-test-client", version: "0.2.0" });
  await client.connect(transport);
  t.after(async () => {
    await client.close();
    await transport.close();
  });
  return client;
}
