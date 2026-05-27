/**
 * High-level config loader.
 *
 * Accepts a path to either a single .md file or a directory containing .md
 * files. For directory mode every *.md is loaded and merged. .env files
 * (cwd and beside the config) are loaded before env-var expansion.
 */

import { readdir, readFile, stat } from "node:fs/promises";
import path from "node:path";
import { config as loadDotenv } from "dotenv";
import { extractYamlBlocks, type RawBlock } from "./parser.js";
import { validateBlocks } from "./validate.js";
import { expandEnv } from "./env.js";
import type { BridgeConfig } from "./types.js";

export interface LoadResult {
  config: BridgeConfig;
  files: string[];
}

export async function loadConfig(configPath: string): Promise<LoadResult> {
  const abs = path.resolve(configPath);
  const st = await stat(abs);

  // Load .env from CWD and from beside the config.
  loadDotenv({ override: false });
  const sideEnv = path.join(st.isDirectory() ? abs : path.dirname(abs), ".env");
  loadDotenv({ path: sideEnv, override: false });

  const files = st.isDirectory() ? await listMarkdownFiles(abs) : [abs];
  if (files.length === 0) {
    throw new Error(`No .md files found at ${configPath}`);
  }

  const allBlocks: RawBlock[] = [];
  for (const file of files) {
    const text = await readFile(file, "utf-8");
    const blocks = extractYamlBlocks(text, file);
    // Expand env vars on each parsed block.
    for (const b of blocks) {
      b.parsed = expandEnv(b.parsed);
    }
    allBlocks.push(...blocks);
  }

  const config = validateBlocks(allBlocks);
  return { config, files };
}

async function listMarkdownFiles(dir: string): Promise<string[]> {
  const entries = await readdir(dir, { withFileTypes: true });
  return entries
    .filter((e) => e.isFile() && e.name.toLowerCase().endsWith(".md"))
    .map((e) => path.join(dir, e.name))
    .sort();
}
