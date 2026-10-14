import assert from "node:assert/strict";
import { setTimeout as pause } from "node:timers/promises";
import test from "node:test";
import { callRevit } from "../build/revit-client.js";
import { allTools } from "../build/tools/index.js";
import { bridge, mcpClient } from "./helpers.mjs";

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

test("MCP lists tools with annotations and an input schema", async (t) => {
  const options = await bridge(t, () => {});
  const client = await mcpClient(t, options.port);
  const { tools } = await client.listTools();
  assert.deepEqual(tools.map((tool) => tool.name).sort(), allTools.map((tool) => tool.name).sort());
  for (const tool of tools) {
    assert.equal(typeof tool.annotations.readOnlyHint, "boolean", tool.name);
    assert.equal(typeof tool.annotations.destructiveHint, "boolean", tool.name);
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

test("commit_plan forwards planId and approvalId and rejects malformed approval IDs locally", async (t) => {
  const requests = [];
  const options = await bridge(t, (socket, request) => {
    requests.push(request);
    socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { applied: 1 } }) + "\n");
  });
  const client = await mcpClient(t, options.port);
  const args = { planId: "pln_20261014_8f3a2c", approvalId: "apr_3f9c0e7a1b2d4c68" };
  const ok = await client.callTool({ name: "commit_plan", arguments: args });
  assert.equal(ok.isError, false);
  assert.deepEqual(requests[0].params, args);
  assert.equal(requests[0].timeoutMs, 120000);

  for (const approvalId of ["apr_123", "pln_20261014_8f3a2c", "APR_3F9C0E7A1B2D4C68"]) {
    const bad = await client.callTool({ name: "commit_plan", arguments: { ...args, approvalId } });
    assert.equal(bad.isError, true, approvalId);
  }
  assert.equal(requests.length, 1);
});

test("get_elements forwards filters and paging unchanged", async (t) => {
  let received;
  const page = { total: 120, offset: 50, limit: 50, returned: 50, hasMore: true, nextOffset: 100, items: [] };
  const options = await bridge(t, (socket, request) => {
    received = request;
    socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: page }) + "\n");
  });
  const client = await mcpClient(t, options.port);
  const args = { categories: ["OST_Walls", "Doors"], categoryType: "Model", levelId: "311", nameContains: "Tường", limit: 50, offset: 50 };
  const result = await client.callTool({ name: "get_elements", arguments: args });
  assert.equal(result.isError, false);
  assert.deepEqual(JSON.parse(result.content[0].text), page);
  assert.equal(received.method, "get_elements");
  assert.deepEqual(received.params, args);
  assert.equal(received.timeoutMs, 60000);
});

test("read tools reject out-of-range paging and malformed element IDs", async (t) => {
  let contacted = false;
  const options = await bridge(t, (socket) => { contacted = true; socket.destroy(); });
  const client = await mcpClient(t, options.port);
  const invalidCalls = [
    { name: "get_elements", arguments: { limit: 501 } },
    { name: "get_elements", arguments: { offset: -1 } },
    { name: "get_elements", arguments: { levelId: "Level 1" } },
    { name: "get_elements", arguments: { categoryType: "Internal" } },
    { name: "list_categories", arguments: { categoryType: "model" } },
    { name: "get_element_parameters", arguments: { elementIds: [] } },
    { name: "get_element_parameters", arguments: { elementIds: Array.from({ length: 51 }, (_, i) => String(i)) } },
    { name: "get_element_parameters", arguments: { elementIds: [12345] } },
  ];
  for (const call of invalidCalls) {
    const result = await client.callTool(call);
    assert.equal(result.isError, true, JSON.stringify(call));
  }
  assert.equal(contacted, false);
});
