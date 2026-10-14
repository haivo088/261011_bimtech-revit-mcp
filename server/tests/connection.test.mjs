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
  return { host: "127.0.0.1", port: server.address().port, timeoutMs: 2000, graceMs: 0 };
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
  assert.deepEqual(await callRevit("get_document_info", {}, options), expected);
});

test("reports a Revit JSON-RPC error", async (t) => {
  const options = await bridge(t, (socket, request) => socket.end(JSON.stringify({
    jsonrpc: "2.0", id: request.id, error: { code: -32001, message: "No model open" },
  }) + "\n"));
  await assert.rejects(callRevit("get_document_info", {}, options), /-32001: No model open/);
});

test("rejects a response belonging to another request", async (t) => {
  const options = await bridge(t, (socket) => socket.end(JSON.stringify({
    jsonrpc: "2.0", id: "wrong-id", result: {},
  }) + "\n"));
  await assert.rejects(callRevit("ping", {}, options), /mismatched request ID/);
});

test("fails promptly when the peer closes with a partial response", async (t) => {
  const options = await bridge(t, (socket) => socket.end('{"jsonrpc":'));
  await assert.rejects(callRevit("ping", {}, options), /closed the connection/);
});

test("times out when the peer never replies", async (t) => {
  const options = await bridge(t, () => {});
  await assert.rejects(callRevit("ping", {}, { ...options, timeoutMs: 50 }), /within 50 ms/);
});

test("sends params and the per-request timeout to the add-in", async (t) => {
  let received;
  const options = await bridge(t, (socket, request) => {
    received = request;
    socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: {} }) + "\n");
  });
  await callRevit("ping", { message: "Xin chào", limit: 5 }, { ...options, timeoutMs: 1234 });
  assert.equal(received.method, "ping");
  assert.deepEqual(received.params, { message: "Xin chào", limit: 5 });
  assert.equal(received.timeoutMs, 1234);
});

test("waits the grace period so the add-in's own timeout error arrives", async (t) => {
  const options = await bridge(t, async (socket, request) => {
    await pause(request.timeoutMs);
    socket.end(JSON.stringify({
      jsonrpc: "2.0", id: request.id, error: { code: -32003, message: "Timed out in Revit" },
    }) + "\n");
  });
  await assert.rejects(
    callRevit("ping", {}, { ...options, timeoutMs: 50, graceMs: 1000 }),
    /-32003: Timed out in Revit/,
  );
});

test("keeps the error code and details reported by the add-in", async (t) => {
  const options = await bridge(t, (socket, request) => socket.end(JSON.stringify({
    jsonrpc: "2.0", id: request.id,
    error: { code: -32602, message: "Invalid params", data: { param: "limit" } },
  }) + "\n"));
  await assert.rejects(callRevit("ping", {}, options), (error) => {
    assert.equal(error.name, "RevitError");
    assert.equal(error.code, -32602);
    assert.deepEqual(error.data, { param: "limit" });
    return true;
  });
});

// Starts the built MCP server as Claude would and points it at the fake bridge.
async function mcpClient(t, port) {
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [fileURLToPath(new URL("../build/index.js", import.meta.url))],
    env: { ...process.env, REVIT_MCP_PORT: String(port) },
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

test("MCP lists tools with annotations and an input schema", async (t) => {
  const options = await bridge(t, () => {});
  const client = await mcpClient(t, options.port);
  const { tools } = await client.listTools();
  assert.deepEqual(tools.map((tool) => tool.name).sort(), [
    "cancel_plan", "get_document_info", "get_plan", "get_plan_status", "open_plan_review", "ping_revit",
    "propose_set_parameters",
  ]);
  // No tool can change the model yet: propose_* only saves a plan, cancel_plan only changes a plan.
  for (const tool of tools) {
    assert.equal(tool.annotations.readOnlyHint, tool.name !== "cancel_plan", tool.name);
    assert.equal(tool.annotations.destructiveHint, false, tool.name);
    assert.equal(tool.inputSchema.type, "object", tool.name);
  }
  const ping = tools.find((tool) => tool.name === "ping_revit");
  assert.equal(ping.inputSchema.properties.message.type, "string");
});

test("MCP forwards tool arguments as params and reports Revit failures", async (t) => {
  const requests = [];
  const options = await bridge(t, (socket, request) => {
    requests.push(request);
    const payload = request.method === "ping"
      ? { result: { status: "ok", revitVersion: "2025", echo: request.params.message } }
      : { error: { code: -32001, message: "No model open" } };
    socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, ...payload }) + "\n");
  });
  const client = await mcpClient(t, options.port);

  const ping = await client.callTool({ name: "ping_revit", arguments: { message: "Dự án A" } });
  assert.equal(ping.isError, false);
  assert.deepEqual(JSON.parse(ping.content[0].text), { status: "ok", revitVersion: "2025", echo: "Dự án A" });
  assert.equal(requests[0].timeoutMs, 5000, "ping_revit uses its own timeout");

  const failure = await client.callTool({ name: "get_document_info", arguments: {} });
  assert.equal(failure.isError, true);
  assert.match(failure.content[0].text, /-32001: No model open/);
  assert.deepEqual(requests[1].params, {});
});

test("MCP rejects invalid arguments before contacting Revit", async (t) => {
  let contacted = false;
  const options = await bridge(t, (socket) => { contacted = true; socket.destroy(); });
  const client = await mcpClient(t, options.port);
  const result = await client.callTool({ name: "ping_revit", arguments: { message: "x".repeat(201) } });
  assert.equal(result.isError, true);
  assert.equal(contacted, false);
});

test("MCP rejects a malformed plan ID before contacting Revit", async (t) => {
  let contacted = false;
  const options = await bridge(t, (socket) => { contacted = true; socket.destroy(); });
  const client = await mcpClient(t, options.port);
  const result = await client.callTool({ name: "get_plan", arguments: { planId: "../../secret" } });
  assert.equal(result.isError, true);
  assert.equal(contacted, false);
});

test("MCP forwards plan filters to the add-in unchanged", async (t) => {
  let received;
  const options = await bridge(t, (socket, request) => {
    received = request;
    socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { items: [] } }) + "\n");
  });
  const client = await mcpClient(t, options.port);
  const args = { planId: "pln_20261014_8f3a2c", tier: "Blocked", limit: 20 };
  const result = await client.callTool({ name: "get_plan", arguments: args });
  assert.equal(result.isError, false);
  assert.equal(received.method, "get_plan");
  assert.deepEqual(received.params, args);
});
