/**
 * Pulls fenced ```yaml ... ``` blocks out of a markdown file, preserving the
 * starting line number so validation errors can point to the source.
 *
 * We deliberately don't parse the markdown itself — we only care about fenced
 * blocks with the "yaml" language tag. Text outside blocks is documentation.
 */

import { parse as parseYaml } from "yaml";
import type { BlockSource } from "./types.js";

export interface RawBlock {
  source: BlockSource;
  parsed: unknown;
  raw: string;
}

/**
 * Find every ```yaml fenced block in the given text.
 * Returns blocks paired with the line number where ```yaml begins.
 */
export function extractYamlBlocks(text: string, file: string): RawBlock[] {
  const lines = text.split(/\r?\n/);
  const blocks: RawBlock[] = [];

  let inBlock = false;
  let blockStartLine = 0;
  let blockLines: string[] = [];
  let blockIndex = 0;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];

    if (!inBlock) {
      // Recognize ```yaml, ```YAML, ``` yaml, optionally with extra info after.
      if (/^\s*```\s*yaml\b/i.test(line)) {
        inBlock = true;
        blockStartLine = i + 1; // 1-based
        blockLines = [];
      }
      continue;
    }

    // Inside a block: closing fence is a line that is exactly ``` (possibly indented).
    if (/^\s*```\s*$/.test(line)) {
      const raw = blockLines.join("\n");
      blockIndex++;
      const source: BlockSource = { file, line: blockStartLine, blockIndex };

      try {
        const parsed = parseYaml(raw);
        // Empty YAML blocks are silently skipped.
        if (parsed !== null && parsed !== undefined) {
          blocks.push({ source, parsed, raw });
        }
      } catch (err) {
        const msg = err instanceof Error ? err.message : String(err);
        throw new ConfigParseError(
          `YAML parse error in ${file} at line ${blockStartLine}: ${msg}`,
          source
        );
      }

      inBlock = false;
      blockLines = [];
      continue;
    }

    blockLines.push(line);
  }

  if (inBlock) {
    throw new ConfigParseError(
      `Unterminated \`\`\`yaml block in ${file} starting at line ${blockStartLine}`,
      { file, line: blockStartLine, blockIndex: blockIndex + 1 }
    );
  }

  return blocks;
}

export class ConfigParseError extends Error {
  constructor(
    message: string,
    public readonly source?: BlockSource
  ) {
    super(message);
    this.name = "ConfigParseError";
  }
}
