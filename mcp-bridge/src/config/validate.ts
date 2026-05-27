import { z } from "zod";
import type {
  BridgeConfig,
  ConnectionDef,
  GlobalConfigDef,
  ToolDef,
} from "./types.js";
import { ConfigParseError, type RawBlock } from "./parser.js";

// ============================================================
// Zod schemas
// ============================================================

const paramTypeSchema = z.enum([
  "string",
  "integer",
  "number",
  "boolean",
  "array",
  "object",
  "file",
]);

const paramSchema = z.object({
  in: z.enum(["path", "query", "body", "form", "header"]),
  type: paramTypeSchema,
  required: z.boolean().optional(),
  default: z.unknown().optional(),
  enum: z.array(z.unknown()).optional(),
  description: z.string().optional(),
  items: z
    .object({
      type: z.enum(["string", "integer", "number", "boolean"]),
    })
    .optional(),
});

const authNone = z.object({ type: z.literal("none") });
const authHeader = z.object({
  type: z.literal("header"),
  header: z.string().min(1),
  value: z.string(),
});
const authBearer = z.object({
  type: z.literal("bearer"),
  token: z.string().min(1),
});
const authBasic = z.object({
  type: z.literal("basic"),
  username: z.string(),
  password: z.string(),
});
const authOauth2 = z.object({
  type: z.literal("oauth2_client_credentials"),
  token_url: z.string().url(),
  client_id: z.string().min(1),
  client_secret: z.string().min(1),
  scope: z.string().optional(),
  client_auth: z.enum(["body", "basic"]).optional(),
});
const authSchema = z.discriminatedUnion("type", [
  authNone,
  authHeader,
  authBearer,
  authBasic,
  authOauth2,
]);

const connectionSchema = z.object({
  type: z.literal("connection"),
  id: z.string().regex(/^[a-zA-Z0-9_-]+$/, {
    message:
      "id must contain only letters, digits, underscore, or dash (no spaces)",
  }),
  base_url: z.string().url(),
  auth: authSchema.optional(),
  default_headers: z.record(z.string(), z.string()).optional(),
  timeout_ms: z.number().int().positive().optional(),
});

const toolSchema = z.object({
  type: z.literal("tool"),
  name: z.string().regex(/^[a-zA-Z0-9_-]+$/, {
    message:
      "tool name must contain only letters, digits, underscore, or dash",
  }),
  description: z.string().min(1, { message: "description is required" }),
  connection: z.string().min(1),
  method: z.enum(["GET", "POST", "PUT", "PATCH", "DELETE"]),
  path: z.string().startsWith("/", { message: "path must start with /" }),
  content_type: z.string().optional(),
  params: z.record(z.string(), paramSchema).optional(),
  body_template: z.string().optional(),
  response_transform: z.string().optional(),
  response_hint: z.string().optional(),
  timeout_ms: z.number().int().positive().optional(),
});

const globalConfigSchema = z.object({
  type: z.literal("config"),
  log_level: z.enum(["debug", "info", "warn", "error"]).optional(),
  default_timeout_ms: z.number().int().positive().optional(),
  max_response_bytes: z.number().int().nonnegative().optional(),
});

// ============================================================
// Public entry: turn raw blocks into a validated BridgeConfig.
// ============================================================

export function validateBlocks(blocks: RawBlock[]): BridgeConfig {
  const connections = new Map<string, ConnectionDef>();
  const tools = new Map<string, ToolDef>();
  let globalConfig: GlobalConfigDef = { type: "config" };

  for (const block of blocks) {
    const parsed = block.parsed;
    if (!parsed || typeof parsed !== "object") {
      throw new ConfigParseError(
        `Block at ${block.source.file}:${block.source.line} is not a YAML object.`,
        block.source
      );
    }

    const typeField = (parsed as { type?: unknown }).type;
    if (typeof typeField !== "string") {
      throw new ConfigParseError(
        `Block at ${block.source.file}:${block.source.line} is missing a 'type' field. Expected one of: config, connection, tool.`,
        block.source
      );
    }

    switch (typeField) {
      case "config": {
        const result = globalConfigSchema.safeParse(parsed);
        if (!result.success) {
          throw formatZodError(result.error, block, "config");
        }
        // Last config block wins for any duplicated fields.
        globalConfig = { ...globalConfig, ...result.data };
        break;
      }

      case "connection": {
        const result = connectionSchema.safeParse(parsed);
        if (!result.success) {
          throw formatZodError(result.error, block, "connection");
        }
        const conn = result.data as ConnectionDef;
        if (connections.has(conn.id)) {
          throw new ConfigParseError(
            `Duplicate connection id '${conn.id}' at ${block.source.file}:${block.source.line}.`,
            block.source
          );
        }
        connections.set(conn.id, conn);
        break;
      }

      case "tool": {
        const result = toolSchema.safeParse(parsed);
        if (!result.success) {
          throw formatZodError(result.error, block, "tool");
        }
        const tool = result.data as ToolDef;
        if (tools.has(tool.name)) {
          throw new ConfigParseError(
            `Duplicate tool name '${tool.name}' at ${block.source.file}:${block.source.line}.`,
            block.source
          );
        }
        tools.set(tool.name, tool);
        break;
      }

      default:
        throw new ConfigParseError(
          `Unknown block type '${typeField}' at ${block.source.file}:${block.source.line}. Expected one of: config, connection, tool.`,
          block.source
        );
    }
  }

  // Cross-block validation: every tool must reference a known connection.
  for (const tool of tools.values()) {
    if (!connections.has(tool.connection)) {
      throw new ConfigParseError(
        `Tool '${tool.name}' references unknown connection '${tool.connection}'. ` +
          `Known connections: ${[...connections.keys()].join(", ") || "(none)"}.`
      );
    }

    // Path params declared on the tool must be in params with in: path.
    const pathParams = extractPathParamNames(tool.path);
    for (const p of pathParams) {
      const def = tool.params?.[p];
      if (!def) {
        throw new ConfigParseError(
          `Tool '${tool.name}': path uses '{${p}}' but params.${p} is not declared.`
        );
      }
      if (def.in !== "path") {
        throw new ConfigParseError(
          `Tool '${tool.name}': params.${p}.in must be 'path' because the path contains '{${p}}'.`
        );
      }
    }
  }

  return { globalConfig, connections, tools };
}

function extractPathParamNames(path: string): string[] {
  const out: string[] = [];
  const re = /\{([a-zA-Z_][a-zA-Z0-9_]*)\}/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(path)) !== null) {
    out.push(m[1]);
  }
  return out;
}

function formatZodError(
  error: z.ZodError,
  block: RawBlock,
  kind: string
): ConfigParseError {
  const issues = error.issues
    .map((i) => {
      const where = i.path.length > 0 ? i.path.join(".") : "(root)";
      return `  - ${where}: ${i.message}`;
    })
    .join("\n");

  return new ConfigParseError(
    `Invalid ${kind} block at ${block.source.file}:${block.source.line}:\n${issues}`,
    block.source
  );
}
