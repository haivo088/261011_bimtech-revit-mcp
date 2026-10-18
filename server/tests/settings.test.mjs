import assert from "node:assert/strict";
import { mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { setTimeout as pause } from "node:timers/promises";
import test from "node:test";
import { allTools, serverOnlyToolNames } from "../build/tools/index.js";
import { bridge, mcpClient } from "./helpers.mjs";

const everyMethod = allTools.map((tool) => tool.method);

// A ping result as the add-in sends it, with the given methods unavailable.
function pingResult(unavailable = []) {
  return {
    status: "ok",
    commands: everyMethod.map((method) => ({ method, available: !unavailable.includes(method) })),
  };
}

function reply(socket, request, payload) {
  socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, ...payload }) + "\n");
}

async function visibleTools(client) {
  return (await client.listTools()).tools.map((tool) => tool.name);
}

// Waits until the tool list matches, since the server hides tools asynchronously.
async function waitFor(check, timeoutMs = 3000) {
  const deadline = Date.now() + timeoutMs;
  while (!(await check())) {
    if (Date.now() > deadline) return false;
    await pause(50);
  }
  return true;
}

test("tools switched off in Revit are hidden at startup", async (t) => {
  const options = await bridge(t, (socket, request) =>
    reply(socket, request, { result: pingResult(["propose_columns", "undo_batch"]) }));
  const client = await mcpClient(t, options.port, { REVIT_MCP_AVAILABILITY_POLL_MS: "60000" });
  const hidden = await waitFor(async () => !(await visibleTools(client)).includes("propose_columns"));
  assert.ok(hidden, "propose_columns should be hidden");
  const tools = await visibleTools(client);
  assert.ok(!tools.includes("undo_batch"));
  assert.ok(tools.includes("get_elements"));
});

test("a 'command disabled' error makes the server re-check and hide the tool", async (t) => {
  let disabled = false;
  const options = await bridge(t, (socket, request) => {
    if (request.method === "ping") return reply(socket, request, { result: pingResult(disabled ? ["get_levels"] : []) });
    disabled = true; // The user switched get_levels off after the server started.
    reply(socket, request, { error: { code: -32008, message: "'get_levels' is turned off", data: { reason: "disabled" } } });
  });
  const client = await mcpClient(t, options.port, { REVIT_MCP_AVAILABILITY_POLL_MS: "60000" });
  assert.ok(await waitFor(async () => (await visibleTools(client)).includes("get_levels")));

  const result = await client.callTool({ name: "get_levels", arguments: {} });
  assert.equal(result.isError, true);
  assert.match(result.content[0].text, /-32008/);
  assert.ok(await waitFor(async () => !(await visibleTools(client)).includes("get_levels")), "get_levels should be hidden");
});

test("all tools stay visible when Revit cannot be reached", async (t) => {
  const options = await bridge(t, (socket) => socket.destroy());
  const client = await mcpClient(t, options.port, { REVIT_MCP_AVAILABILITY_POLL_MS: "60000" });
  await pause(300);
  assert.deepEqual((await visibleTools(client)).sort(), [...allTools.map((tool) => tool.name), ...serverOnlyToolNames].sort());
});

test("the server reads the bridge port from settings.json", async (t) => {
  const options = await bridge(t, (socket, request) => reply(socket, request, { result: { status: "ok", revitVersion: "2025" } }));
  const directory = await mkdtemp(join(tmpdir(), "bimtech-settings-"));
  const settingsPath = join(directory, "settings.json");
  // Written with a BOM, as some Windows editors do.
  await writeFile(settingsPath, "\uFEFF" + JSON.stringify({ port: options.port, readOnlyMode: false }), "utf8");
  const client = await mcpClient(t, options.port, { REVIT_MCP_PORT: undefined, REVIT_MCP_SETTINGS: settingsPath });
  const result = await client.callTool({ name: "ping_revit", arguments: {} });
  assert.equal(result.isError, false, result.content[0].text);
});
