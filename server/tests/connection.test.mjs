import assert from "node:assert/strict";
import { createServer } from "node:net";
import { once } from "node:events";
import { setTimeout as pause } from "node:timers/promises";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { callRevit } from "../build/revit-client.js";

// A small TCP stand-in verifies transport behavior without opening Revit.
async function bridge(t, respond) {
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
  return { host: "127.0.0.1", port: server.address().port, timeoutMs: 2000 };
}

test("reads a large response split inside a Vietnamese UTF-8 character", async (t) => {
  const expected = { title: "Dự án hạ tầng", content: "Việt Nam ".repeat(3000) };
  const options = await bridge(t, async (socket, request) => {
    const bytes = Buffer.from(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: expected }) + "\n");
    const split = bytes.indexOf(Buffer.from("ạ")) + 1;
    socket.write(bytes.subarray(0, split));
    await pause(10);
    socket.end(bytes.subarray(split));
  });
  assert.deepEqual(await callRevit("get_document_info", options), expected);
});

test("reports a Revit JSON-RPC error", async (t) => {
  const options = await bridge(t, (socket, request) => socket.end(JSON.stringify({
    jsonrpc: "2.0", id: request.id, error: { code: -32001, message: "No model open" },
  }) + "\n"));
  await assert.rejects(callRevit("get_document_info", options), /-32001: No model open/);
});

test("rejects a response belonging to another request", async (t) => {
  const options = await bridge(t, (socket) => socket.end(JSON.stringify({
    jsonrpc: "2.0", id: "wrong-id", result: {},
  }) + "\n"));
  await assert.rejects(callRevit("ping", options), /mismatched request ID/);
});

test("fails promptly when the peer closes with a partial response", async (t) => {
  const options = await bridge(t, (socket) => socket.end('{"jsonrpc":'));
  await assert.rejects(callRevit("ping", options), /closed the connection/);
});

test("times out when the peer never replies", async (t) => {
  const options = await bridge(t, () => {});
  await assert.rejects(callRevit("ping", { ...options, timeoutMs: 50 }), /within 50 ms/);
});

test("MCP discovers two tools and forwards success and failure over TCP", async (t) => {
  const options = await bridge(t, (socket, request) => {
    const payload = request.method === "ping"
      ? { result: { status: "ok", revitVersion: "2025" } }
      : { error: { code: -32001, message: "No model open" } };
    socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, ...payload }) + "\n");
  });
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [fileURLToPath(new URL("../build/index.js", import.meta.url))],
    env: { ...process.env, REVIT_MCP_PORT: String(options.port) },
    stderr: "pipe",
  });
  let diagnostics = "";
  transport.stderr?.on("data", (chunk) => { diagnostics += chunk; });
  const client = new Client({ name: "bimtech-test-client", version: "0.1.0" });
  try {
    await client.connect(transport);
    const { tools } = await client.listTools();
    assert.deepEqual(tools.map((tool) => tool.name).sort(), ["get_document_info", "ping_revit"]);
    const ping = await client.callTool({ name: "ping_revit", arguments: {} });
    assert.equal(JSON.parse(ping.content[0].text).revitVersion, "2025");
    const failure = await client.callTool({ name: "get_document_info", arguments: {} });
    assert.equal(failure.isError, true);
    assert.match(failure.content[0].text, /No model open/);
  } catch (error) {
    console.error(diagnostics);
    throw error;
  } finally {
    await client.close();
    await transport.close();
  }
});
