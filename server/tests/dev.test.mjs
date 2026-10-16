import assert from "node:assert/strict";
import test from "node:test";
import { bridge, mcpClient } from "./helpers.mjs";

const developer = {
  BIMTECH_DEV: "1",
  BIMTECH_CASES_DIR: "D:\\cases-for-test",
  BIMTECH_COMMANDSETS_DIR: "D:\\sets-for-test",
  BIMTECH_CODE_HISTORY: "D:\\runs-for-test.jsonl",
};
const folders = { commandSetRoot: "D:\\sets-for-test", historyFile: "D:\\runs-for-test.jsonl" };

function recordingBridge(t, requests) {
  return bridge(t, (socket, request) => {
    requests.push(request);
    socket.end(JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { ok: true } }) + "\n");
  });
}

test("developer tools forward their input, and the server adds its folders", async (t) => {
  const requests = [];
  const options = await recordingBridge(t, requests);
  const client = await mcpClient(t, options.port, developer);
  const query = { code: "return doc.Title;", maxResultChars: 2000 };
  assert.equal((await client.callTool({ name: "send_code", arguments: query })).isError, false);
  const script = { script: "family-tcvn/rename-params", args: { prefix: "BTC_", ids: ["123"] }, write: true };
  assert.equal((await client.callTool({ name: "send_code", arguments: script })).isError, false);
  const save = {
    action: "save", script: "checks/empty-marks", description: "Cột chưa có Mark", code: "return 0;",
    params: [{ name: "category", type: "string", default: "Structural Columns" }],
  };
  assert.equal((await client.callTool({ name: "command_set", arguments: save })).isError, false);
  const exportArgs = { cadId: "501", levelId: "694", notes: "Bản vẽ móng mẫu", includeTruth: false };
  // The case folder is set by the server; a caseRoot from the caller is dropped.
  const withFolder = { ...exportArgs, caseRoot: "C:\\Windows" };
  assert.equal((await client.callTool({ name: "export_cad_case", arguments: withFolder })).isError, false);

  assert.deepEqual(requests.map((request) => request.method), ["send_code", "send_code", "command_set", "export_cad_case"]);
  assert.deepEqual(requests[0].params, { ...query, ...folders });
  assert.deepEqual(requests[1].params, { ...script, ...folders });
  assert.deepEqual(requests[2].params, { ...save, ...folders });
  assert.deepEqual(requests[3].params, { ...exportArgs, caseRoot: "D:\\cases-for-test" });
});

test("developer tools reject malformed input before contacting Revit", async (t) => {
  let contacted = false;
  const options = await bridge(t, (socket) => { contacted = true; socket.destroy(); });
  const client = await mcpClient(t, options.port, developer);
  const invalidCalls = [
    { name: "send_code", arguments: { code: "" } },
    { name: "send_code", arguments: { script: "Bad Name" } },
    { name: "send_code", arguments: { code: "return 1;", timeoutSeconds: 500 } },
    { name: "command_set", arguments: {} },
    { name: "command_set", arguments: { action: "delete" } },
    { name: "command_set", arguments: { action: "save", params: [{ name: "x", type: "date" }] } },
    { name: "export_cad_case", arguments: {} },
    { name: "export_cad_case", arguments: { cadId: "CAD-1" } },
  ];
  for (const call of invalidCalls) {
    const result = await client.callTool(call);
    assert.equal(result.isError, true, JSON.stringify(call));
  }
  assert.equal(contacted, false);
});
