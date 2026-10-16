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

test("beam placement and pile proposals forward their options", async (t) => {
  const requests = [];
  const options = await bridge(t, (socket, request) => {
    requests.push(request);
    socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { dryRun: true } }) + "\n");
  });
  const client = await mcpClient(t, options.port);
  const beamArgs = { scanId: "scan-2", levelId: "694", include: ["220x600"], exclude: ["D9"], tagParameterName: "Mark" };
  assert.equal((await client.callTool({ name: "place_beams", arguments: beamArgs })).isError, false);
  const pileArgs = {
    scanId: "scan-3", capLevelId: "311", withLot: false, createMissingTypes: true, embedmentMm: 150,
    capTypes: { "Đ1": "12345" }, capThicknessMm: { "2000x2000": 1200 }, pileLengthMm: 32000, snapMm: 5, join: false,
  };
  assert.equal((await client.callTool({ name: "propose_piles", arguments: pileArgs })).isError, false);
  assert.deepEqual(requests.map((request) => request.method), ["place_beams", "propose_piles"]);
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
    { name: "propose_piles", arguments: { scanId: "scan-1", capLevelId: "311", embedmentMm: -1 } },
    { name: "propose_piles", arguments: { scanId: "scan-1", capLevelId: "311", capThicknessMm: { "Đ1": 20 } } },
    { name: "propose_piles", arguments: { scanId: "scan-1", capLevelId: "311", capTypes: { "Đ1": 12345 } } },
  ];
  for (const call of invalidCalls) {
    const result = await client.callTool(call);
    assert.equal(result.isError, true, JSON.stringify(call));
  }
  assert.equal(contacted, false);
});

test("level and grid proposals forward their input unchanged", async (t) => {
  const requests = [];
  const options = await bridge(t, (socket, request) => {
    requests.push(request);
    socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { plan: null } }) + "\n");
  });
  const client = await mcpClient(t, options.port);
  const levelArgs = {
    levels: [
      { name: "Tầng 1", elevationMm: 0, cadFile: "D:\DuAn\S-101.DWG" },
      { name: "Tầng 2", elevationMm: 3600, cadFile: "D:\DuAn\S-201.dwg", mode: "link" },
      { name: "Mái", elevationMm: 7200, cadUnit: "auto" },
    ],
    viewTemplate: "KC - Mặt bằng",
    scale: 100,
    proposeLevelCleanup: false,
  };
  assert.equal((await client.callTool({ name: "propose_levels", arguments: levelArgs })).isError, false);
  const gridArgs = {
    cadId: "501", alignCadIds: ["502", "503"], gridLayer: "KC-TRUC", snapModuleMm: 100, snapToleranceMm: 5, pin: false,
  };
  assert.equal((await client.callTool({ name: "propose_grids", arguments: gridArgs })).isError, false);
  assert.deepEqual(requests.map((request) => request.method), ["propose_levels", "propose_grids"]);
  assert.deepEqual(requests[0].params, levelArgs);
  assert.deepEqual(requests[1].params, gridArgs);
});

test("level and grid proposals reject malformed input before contacting Revit", async (t) => {
  let contacted = false;
  const options = await bridge(t, (socket) => { contacted = true; socket.destroy(); });
  const client = await mcpClient(t, options.port);
  const invalidCalls = [
    { name: "propose_levels", arguments: {} },
    { name: "propose_levels", arguments: { levels: [] } },
    { name: "propose_levels", arguments: { levels: [{ name: "", elevationMm: 0 }] } },
    { name: "propose_levels", arguments: { levels: [{ name: "Tầng 1" }] } },
    { name: "propose_levels", arguments: { levels: [{ name: "Tầng 1", elevationMm: 0, cadFile: "D:\S-101.pdf" }] } },
    { name: "propose_levels", arguments: { levels: [{ name: "Tầng 1", elevationMm: 0, mode: "copy" }] } },
    { name: "propose_levels", arguments: { levels: [{ name: "Tầng 1", elevationMm: 0, cadUnit: "inch" }] } },
    { name: "propose_levels", arguments: { levels: [{ name: "Tầng 1", elevationMm: 0 }], scale: 0 } },
    { name: "suggest_levels_from_folder", arguments: {} },
    { name: "propose_grids", arguments: {} },
    { name: "propose_grids", arguments: { cadId: "501", alignCadIds: ["CAD-2"] } },
    { name: "propose_grids", arguments: { cadId: "501", snapToleranceMm: 500 } },
    { name: "propose_grids", arguments: { cadId: "501", gridLayer: "" } },
  ];
  for (const call of invalidCalls) {
    const result = await client.callTool(call);
    assert.equal(result.isError, true, JSON.stringify(call));
  }
  assert.equal(contacted, false);
});

test("wall and floor tools forward their input unchanged", async (t) => {
  const requests = [];
  const options = await bridge(t, (socket, request) => {
    requests.push(request);
    socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { plan: null } }) + "\n");
  });
  const client = await mcpClient(t, options.port);
  const calls = [
    { name: "scan_cad_walls", arguments: { cadId: "501", layers: ["S-VACH"], textSearchMm: 800 } },
    {
      name: "propose_walls",
      arguments: {
        scanId: "scan-4", baseLevelId: "311", topLevelId: "694", topOffsetMm: -100, include: ["w200"],
        exclude: ["V9|300"], types: { "V1|200": "12345" }, onlySupported: true, extendToColumns: true,
      },
    },
    { name: "propose_floors", arguments: { levelId: "694", floorTypeId: "777", offsetMm: -50 } },
  ];
  for (const call of calls) assert.equal((await client.callTool(call)).isError, false, call.name);
  assert.deepEqual(requests.map((request) => request.method), calls.map((call) => call.name));
  calls.forEach((call, index) => assert.deepEqual(requests[index].params, call.arguments));
});

test("wall and floor tools reject malformed input before contacting Revit", async (t) => {
  let contacted = false;
  const options = await bridge(t, (socket) => { contacted = true; socket.destroy(); });
  const client = await mcpClient(t, options.port);
  const invalidCalls = [
    { name: "scan_cad_walls", arguments: {} },
    { name: "scan_cad_walls", arguments: { cadId: "1", textSearchMm: 0 } },
    { name: "propose_walls", arguments: { scanId: "scan-1", baseLevelId: "311" } },
    { name: "propose_walls", arguments: { scanId: "scan-1", baseLevelId: "311", topLevelId: "694", types: { w200: 1 } } },
    { name: "propose_floors", arguments: {} },
    { name: "propose_floors", arguments: { levelId: "L2" } },
  ];
  for (const call of invalidCalls) {
    const result = await client.callTool(call);
    assert.equal(result.isError, true, JSON.stringify(call));
  }
  assert.equal(contacted, false);
});
