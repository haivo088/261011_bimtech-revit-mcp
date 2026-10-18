import assert from "node:assert/strict";
import test from "node:test";
import { bridge, mcpClient } from "./helpers.mjs";

const planId = "pln_20261018_95b096";

// A fake add-in that answers get_plan_status with the given statuses in turn, the last one forever.
async function statusBridge(t, statuses, requests = []) {
  let call = 0;
  return bridge(t, (socket, request) => {
    requests.push(request);
    const next = statuses[Math.min(call++, statuses.length - 1)];
    const reply = typeof next === "number"
      ? { jsonrpc: "2.0", id: request.id, error: { code: next, message: "Revit is busy" } }
      : { jsonrpc: "2.0", id: request.id, result: { planId, status: next, approvalId: next === "InReview" ? null : "apr_0123456789abcdef" } };
    socket.end(JSON.stringify(reply) + "\n");
  });
}

const fast = { REVIT_MCP_WAIT_POLL_MS: "20" };

function parse(result) {
  assert.equal(result.isError, false, JSON.stringify(result));
  return JSON.parse(result.content[0].text);
}

test("wait_for_plan returns once the review window wrote the plan", async (t) => {
  const requests = [];
  const options = await statusBridge(t, ["InReview", "InReview", "Committed"], requests);
  const client = await mcpClient(t, options.port, fast);

  const result = parse(await client.callTool({ name: "wait_for_plan", arguments: { planId } }));

  assert.equal(result.status, "Committed");
  assert.equal(result.timedOut, false);
  assert.match(result.next, /go on with the next step/);
  assert.equal(requests.length, 3);
  assert.ok(requests.every((request) => request.method === "get_plan_status" && request.params.planId === planId));
});

test("wait_for_plan keeps waiting while Revit is busy", async (t) => {
  const options = await statusBridge(t, ["InReview", -32003, "Cancelled"]);
  const client = await mcpClient(t, options.port, fast);

  const result = parse(await client.callTool({ name: "wait_for_plan", arguments: { planId } }));

  assert.equal(result.status, "Cancelled");
  assert.match(result.next, /cancelled/);
});

test("wait_for_plan gives up after the timeout and says the plan still waits", async (t) => {
  const options = await statusBridge(t, ["InReview"]);
  const client = await mcpClient(t, options.port, { REVIT_MCP_WAIT_POLL_MS: "200" });

  const result = parse(await client.callTool({ name: "wait_for_plan", arguments: { planId, timeoutSeconds: 10 } }, undefined, { timeout: 30000 }));

  assert.equal(result.status, "InReview");
  assert.equal(result.timedOut, true);
  assert.ok(result.waitedSeconds >= 10);
});

test("wait_for_plan reports other add-in errors at once", async (t) => {
  const options = await statusBridge(t, [-32020]);
  const client = await mcpClient(t, options.port, fast);

  const result = await client.callTool({ name: "wait_for_plan", arguments: { planId } });

  assert.equal(result.isError, true);
});

test("wait_for_plan rejects a malformed plan ID before contacting Revit", async (t) => {
  let contacted = false;
  const options = await bridge(t, (socket) => { contacted = true; socket.destroy(); });
  const client = await mcpClient(t, options.port, fast);

  const result = await client.callTool({ name: "wait_for_plan", arguments: { planId: "plan-1" } });

  assert.equal(result.isError, true);
  assert.equal(contacted, false);
});
