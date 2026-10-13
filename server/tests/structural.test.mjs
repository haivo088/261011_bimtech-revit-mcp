import assert from "node:assert/strict";
import test from "node:test";
import { bridge, mcpClient } from "./helpers.mjs";

test("place_columns defaults to a dry run and forwards the type map", async (t) => {
  const requests = [];
  const options = await bridge(t, (socket, request) => {
    requests.push(request);
    socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { dryRun: true } }) + "\n");
  });
  const client = await mcpClient(t, options.port);
  const args = { scanId: "scan-1", baseLevelId: "311", topLevelId: "694", types: { "500x300": "12345" } };
  const result = await client.callTool({ name: "place_columns", arguments: args });
  assert.equal(result.isError, false);
  assert.deepEqual(requests[0].params, args, "dryRun is omitted, so the add-in default (true) applies");
  assert.equal(requests[0].timeoutMs, 120000);
});

test("write tools are marked as such, read tools are not", async (t) => {
  const options = await bridge(t, () => {});
  const client = await mcpClient(t, options.port);
  const { tools } = await client.listTools();
  const annotations = Object.fromEntries(tools.map((tool) => [tool.name, tool.annotations]));
  assert.equal(annotations.place_columns.readOnlyHint, false);
  assert.equal(annotations.place_columns.destructiveHint, false);
  assert.equal(annotations.undo_batch.readOnlyHint, false);
  assert.equal(annotations.undo_batch.destructiveHint, true);
  assert.equal(annotations.scan_cad_columns.readOnlyHint, true);
  assert.equal(annotations.list_cad_sources.readOnlyHint, true);
});

test("structural tools reject malformed input before contacting Revit", async (t) => {
  let contacted = false;
  const options = await bridge(t, (socket) => { contacted = true; socket.destroy(); });
  const client = await mcpClient(t, options.port);
  const invalidCalls = [
    { name: "scan_cad_columns", arguments: {} },
    { name: "scan_cad_columns", arguments: { cadId: "CAD-1" } },
    { name: "scan_cad_columns", arguments: { cadId: "1", region: { minX: 0, minY: 0, maxX: 1 } } },
    { name: "place_columns", arguments: { scanId: "scan-1", baseLevelId: "311" } },
    { name: "place_columns", arguments: { scanId: "scan-1", baseLevelId: "311", topLevelId: "694", types: { "500x300": 12345 } } },
    { name: "undo_batch", arguments: {} },
  ];
  for (const call of invalidCalls) {
    const result = await client.callTool(call);
    assert.equal(result.isError, true, JSON.stringify(call));
  }
  assert.equal(contacted, false);
});

test("beam and pile placement default to a dry run and forward their options", async (t) => {
  const requests = [];
  const options = await bridge(t, (socket, request) => {
    requests.push(request);
    socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { dryRun: true } }) + "\n");
  });
  const client = await mcpClient(t, options.port);
  const beamArgs = { scanId: "scan-2", levelId: "694", include: ["220x600"], exclude: ["D9"], tagParameterName: "Mark" };
  assert.equal((await client.callTool({ name: "place_beams", arguments: beamArgs })).isError, false);
  const pileArgs = { scanId: "scan-3", capLevelId: "311", withLot: false, createMissingTypes: true, embedmentMm: 150 };
  assert.equal((await client.callTool({ name: "place_piles", arguments: pileArgs })).isError, false);
  assert.deepEqual(requests.map((request) => request.method), ["place_beams", "place_piles"]);
  assert.deepEqual(requests[0].params, beamArgs);
  assert.deepEqual(requests[1].params, pileArgs);
});

test("beam, pile and family tools reject malformed input before contacting Revit", async (t) => {
  let contacted = false;
  const options = await bridge(t, (socket) => { contacted = true; socket.destroy(); });
  const client = await mcpClient(t, options.port);
  const invalidCalls = [
    { name: "load_default_family", arguments: {} },
    { name: "load_default_family", arguments: { kind: "wall" } },
    { name: "scan_cad_beams", arguments: { cadId: "1", textSearchMm: 0 } },
    { name: "place_beams", arguments: { scanId: "scan-1" } },
    { name: "scan_cad_piles", arguments: { cadId: "1", pileLayers: [""] } },
    { name: "place_piles", arguments: { scanId: "scan-1", capLevelId: "311", embedmentMm: -1 } },
  ];
  for (const call of invalidCalls) {
    const result = await client.callTool(call);
    assert.equal(result.isError, true, JSON.stringify(call));
  }
  assert.equal(contacted, false);
});
