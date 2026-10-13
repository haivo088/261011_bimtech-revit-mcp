import type { RegisteredTool } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit } from "./revit-client.js";
import type { RevitTool } from "./tools/define.js";

interface PingCommand {
  method: string;
  available: boolean;
}

// Hides tools the user switched off in Revit's settings window, so Claude does not try them.
// This is a convenience: the add-in refuses disabled commands on its own, whatever the server shows.
// When Revit is unreachable the last known state is kept, since nothing better is known.
export class ToolAvailability {
  private syncing: Promise<void> | undefined;

  constructor(private readonly registered: ReadonlyMap<RevitTool, RegisteredTool>) {}

  // Concurrent callers share one ping instead of sending several.
  sync(): Promise<void> {
    this.syncing ??= this.syncOnce().finally(() => { this.syncing = undefined; });
    return this.syncing;
  }

  private async syncOnce(): Promise<void> {
    let commands: PingCommand[] | undefined;
    try {
      const ping = await callRevit("ping", {}, { timeoutMs: 2000, graceMs: 500 }) as { commands?: PingCommand[] };
      commands = ping.commands;
    } catch {
      return;
    }
    if (!Array.isArray(commands)) return; // Older add-in without settings: leave everything visible.

    const availability = new Map(commands.map((command) => [command.method, command.available]));
    for (const [tool, handle] of this.registered) {
      // A tool whose method the add-in does not know (version mismatch) is hidden too.
      const shouldEnable = availability.get(tool.method) === true;
      if (handle.enabled === shouldEnable) continue;
      if (shouldEnable) handle.enable();
      else handle.disable();
      console.error(`BIMTECH Revit MCP: ${tool.name} ${shouldEnable ? "enabled" : "hidden"} by Revit settings.`);
    }
  }

  // Polls in the background without keeping the process alive.
  startPolling(intervalMs: number): void {
    if (intervalMs <= 0) return;
    setInterval(() => { void this.sync(); }, intervalMs).unref();
  }
}
