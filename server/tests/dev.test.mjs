import assert from "node:assert/strict";
import test from "node:test";
import { bridge, mcpClient } from "./helpers.mjs";

const developer = { BIMTECH_DEV: "1", BIMTECH_CASES_DIR: "D:\\cases-for-test" };

function recordingBridge(t, requests) {
  return bridge(t, (socket, request) => {
    requests.push(request);
    socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { ok: true } }) + "\n");
  });
}

test("developer tools forward their input, and export_cad_case adds the case folder", async (t) => {
  const requests = [];
  const options = await recordingBridge(t, requests);
  const client = await mcpClient(t, options.port, developer);
  const query = { code: "return doc.Title;", maxResultChars: 2000 };
  assert.equal((await client.callTool({ name: "run_revit_query", arguments: query })).isError, false);
  const exportArgs = { cadId: "501", notes: "Bản vẽ móng mẫu", includeTruth: false };
  // The case folder is set by the server; a caseRoot from the caller is dropped.
  const withFolder = { ...exportArgs, caseRoot: "C:\\Windows" };
  assert.equal((await client.callTool({ name: "export_cad_case", arguments: withFolder })).isError, false);

  assert.deepEqual(requests.map((request) => request.method), ["run_revit_query", "export_cad_case"]);
  assert.deepEqual(requests[0].params, query);
  assert.deepEqual(requests[1].params, { ...exportArgs, caseRoot: "D:\\cases-for-test" });
});

test("developer tools reject malformed input before contacting Revit", async (t) => {
  let contacted = false;
  const options = await bridge(t, (socket) => { contacted = true; socket.destroy(); });
  const client = await mcpClient(t, options.port, developer);
  const invalidCalls = [
    { name: "run_revit_query", arguments: {} },
    { name: "run_revit_query", arguments: { code: "" } },
    { name: "export_cad_case", arguments: {} },
    { name: "export_cad_case", arguments: { cadId: "CAD-1" } },
  ];
  for (const call of invalidCalls) {
    const result = await client.callTool(call);
    assert.equal(result.isError, true, JSON.stringify(call));
  }
  assert.equal(contacted, false);
});
