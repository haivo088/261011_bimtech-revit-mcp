import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { allTools } from "../build/tools/index.js";

const commandsDir = fileURLToPath(new URL("../../addin/Commands/", import.meta.url));

// Reads `public string Method => "..."` from every C# command file.
async function addinMethods() {
  const files = (await readdir(commandsDir, { recursive: true })).filter((file) => file.endsWith(".cs"));
  const methods = [];
  for (const file of files) {
    const source = await readFile(join(commandsDir, file), "utf8");
    for (const match of source.matchAll(/public\s+string\s+Method\s*=>\s*"([^"]+)"/g)) methods.push(match[1]);
  }
  return methods.sort();
}

test("every server tool has a matching add-in command and vice versa", async () => {
  const serverMethods = allTools.map((tool) => tool.method).sort();
  assert.deepEqual(serverMethods, await addinMethods());
});

// Reads each command's declared access (Read/Write/Delete) from the C# sources.
async function addinAccess() {
  const files = (await readdir(commandsDir, { recursive: true })).filter((file) => file.endsWith(".cs"));
  const access = new Map();
  for (const file of files) {
    const source = await readFile(join(commandsDir, file), "utf8");
    const method = source.match(/public\s+string\s+Method\s*=>\s*"([^"]+)"/)?.[1];
    const kind = source.match(/public\s+CommandAccess\s+Access\s*=>\s*CommandAccess\.(\w+)/)?.[1];
    if (method) access.set(method, kind);
  }
  return access;
}

test("server annotations match the access each add-in command declares", async () => {
  const access = await addinAccess();
  for (const tool of allTools) {
    const kind = access.get(tool.method);
    assert.ok(kind, `${tool.method} declares no CommandAccess`);
    const { readOnlyHint, destructiveHint } = tool.annotations;
    assert.equal(readOnlyHint, kind === "Read", `${tool.name}: readOnlyHint vs ${kind}`);
    assert.equal(destructiveHint, kind === "Delete", `${tool.name}: destructiveHint vs ${kind}`);
  }
});

test("tool names and methods are unique", () => {
  for (const key of ["name", "method"]) {
    const values = allTools.map((tool) => tool[key]);
    assert.equal(new Set(values).size, values.length, `duplicate ${key}`);
  }
});
