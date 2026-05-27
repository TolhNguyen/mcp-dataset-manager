/**
 * Watches the config path for changes and invokes a callback. Used to
 * hot-reload the tool registry while the MCP server is running.
 *
 * Errors during reload are reported to stderr; the previous valid config
 * keeps serving traffic.
 */

import chokidar from "chokidar";

export interface Watcher {
  close(): Promise<void>;
}

export function watchConfig(
  configPath: string,
  onChange: () => Promise<void>
): Watcher {
  const watcher = chokidar.watch(configPath, {
    ignoreInitial: true,
    awaitWriteFinish: {
      stabilityThreshold: 200,
      pollInterval: 50,
    },
  });

  let reloading = false;
  let pending = false;

  const handle = async () => {
    if (reloading) {
      pending = true;
      return;
    }
    reloading = true;
    try {
      await onChange();
    } finally {
      reloading = false;
      if (pending) {
        pending = false;
        await handle();
      }
    }
  };

  watcher.on("add", handle);
  watcher.on("change", handle);
  watcher.on("unlink", handle);

  return {
    async close() {
      await watcher.close();
    },
  };
}
