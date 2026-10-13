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

test("tool names and methods are unique", () => {
  for (const key of ["name", "method"]) {
    const values = allTools.map((tool) => tool[key]);
    assert.equal(new Set(values).size, values.length, `duplicate ${key}`);
  }
});
