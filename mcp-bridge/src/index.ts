#!/usr/bin/env node
/**
 * mcp-bridge
 *
 * Remote MCP bridge over Streamable HTTP.
 *
 * Modes:
 *   node dist/index.js                — start HTTP MCP server on /mcp
 *   node dist/index.js validate [cfg] — load + validate config, exit
 */

import express, { type NextFunction, type Request, type Response } from "express";
import { randomUUID } from "node:crypto";
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
  isInitializeRequest,
} from "@modelcontextprotocol/sdk/types.js";

import { loadConfig } from "./config/loader.js";
import { ConfigParseError } from "./config/parser.js";
import type { BridgeConfig, ParamDef, ToolDef } from "./config/types.js";
import { executeTool, log, setLogLevel } from "./http/client.js";
import { runWithRequestContext } from "./requestContext.js";
import { watchConfig } from "./watcher.js";

// ============================================================
// Resolve config path
// ============================================================

function resolveConfigPath(): string {
  const cli = process.argv[3];
  if (cli) return cli;

  const fromEnv = process.env.MCP_CONFIG;
  if (fromEnv) return fromEnv;

  return "./tools.md";
}

// ============================================================
// MCP tool definitions <-> bridge tools
// ============================================================

function buildInputSchema(tool: ToolDef): Record<string, unknown> {
  const properties: Record<string, unknown> = {};
  const required: string[] = [];

  for (const [name, def] of Object.entries(tool.params ?? {})) {
    properties[name] = paramToJsonSchema(def);
    if (def.required) required.push(name);
  }

  const schema: Record<string, unknown> = {
    type: "object",
    properties,
  };
  if (required.length > 0) schema.required = required;
  return schema;
}

function paramToJsonSchema(def: ParamDef): Record<string, unknown> {
  const map: Record<string, unknown> = {};
  switch (def.type) {
    case "string":
    case "file":
      map.type = "string";
      break;
    case "integer":
      map.type = "integer";
      break;
    case "number":
      map.type = "number";
      break;
    case "boolean":
      map.type = "boolean";
      break;
    case "array":
      map.type = "array";
      if (def.items) map.items = { type: def.items.type };
      break;
    case "object":
      map.type = "object";
      break;
  }

  if (def.description) map.description = def.description;
  if (def.enum) map.enum = def.enum;
  if (def.default !== undefined) map.default = def.default;
  return map;
}

function compactToolDescription(tool: ToolDef): string {
  return tool.description.trim();
}

function createProtocolServer(getBridgeConfig: () => BridgeConfig): Server {
  const server = new Server(
    { name: "mcp-bridge", version: "1.1.0" },
    {
      capabilities: {
        tools: {
          listChanged: true,
        },
      },
    }
  );

  server.setRequestHandler(ListToolsRequestSchema, async () => {
    const bridgeConfig = getBridgeConfig();
    return {
      tools: [...bridgeConfig.tools.values()].map((t) => ({
        name: t.name,
        description: compactToolDescription(t),
        inputSchema: buildInputSchema(t),
      })),
    };
  });

  server.setRequestHandler(CallToolRequestSchema, async (request) => {
    const bridgeConfig = getBridgeConfig();
    const name = request.params.name;
    const args = (request.params.arguments ?? {}) as Record<string, unknown>;

    const tool = bridgeConfig.tools.get(name);
    if (!tool) {
      return {
        content: [{ type: "text", text: `Unknown tool: ${name}` }],
        isError: true,
      };
    }

    const result = await executeTool(bridgeConfig, tool, args);
    return {
      content: [{ type: "text", text: result.text }],
      isError: result.isError,
    };
  });

  return server;
}

// ============================================================
// Main
// ============================================================

async function main() {
  const command = process.argv[2];

  if (command === "validate") {
    await runValidate();
    return;
  }

  if (command === "--help" || command === "-h") {
    printHelp();
    return;
  }

  await runHttpServer();
}

async function runValidate() {
  const configPath = process.argv[3] ?? process.env.MCP_CONFIG ?? "./tools.md";
  try {
    const { config, files } = await loadConfig(configPath);
    console.error(`Loaded ${files.length} file(s) from ${configPath}:`);
    for (const f of files) console.error(`  - ${f}`);
    console.error("");
    console.error(`Connections (${config.connections.size}):`);
    for (const c of config.connections.values()) {
      console.error(`  - ${c.id} → ${c.base_url} (auth: ${c.auth?.type ?? "none"})`);
    }
    console.error("");
    console.error(`Tools (${config.tools.size}):`);
    for (const t of config.tools.values()) {
      const required = Object.entries(t.params ?? {})
        .filter(([, d]) => d.required)
        .map(([n]) => n);
      const reqStr = required.length ? ` (required: ${required.join(", ")})` : "";
      console.error(`  - ${t.name}: ${t.method} ${t.path}${reqStr}`);
    }
    console.error("\nValidation OK.");
    process.exit(0);
  } catch (e) {
    if (e instanceof ConfigParseError) {
      console.error("Validation failed:\n");
      console.error(e.message);
    } else if (e instanceof Error) {
      console.error(`Validation failed: ${e.message}`);
    } else {
      console.error(`Validation failed: ${String(e)}`);
    }
    process.exit(1);
  }
}

async function runHttpServer() {
  const configPath = resolveConfigPath();
  let bridgeConfig = await loadInitialConfig(configPath);
  applyLogLevel(bridgeConfig);

  const activeSessions = new Map<
    string,
    { transport: StreamableHTTPServerTransport; server: Server }
  >();

  const watcher = watchConfig(configPath, async () => {
    try {
      const result = await loadConfig(configPath);
      bridgeConfig = result.config;
      applyLogLevel(bridgeConfig);
      log("info", `Reloaded ${bridgeConfig.tools.size} tool(s) from ${configPath}.`);

      for (const [sessionId, session] of activeSessions) {
        try {
          await session.server.notification({ method: "notifications/tools/list_changed" });
        } catch (e) {
          log(
            "debug",
            `Failed to notify session ${sessionId}: ${e instanceof Error ? e.message : e}`
          );
        }
      }
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      log("error", `Reload failed; keeping previous config in place.\n${msg}`);
    }
  });

  const app = express();
  app.set("trust proxy", true);
  app.disable("x-powered-by");
  app.use(express.json({ limit: process.env.MCP_JSON_LIMIT ?? "8mb" }));

  app.use((req, res, next) => {
    res.setHeader("Access-Control-Allow-Origin", process.env.CORS_ALLOWED_ORIGIN ?? "*");
    res.setHeader("Access-Control-Allow-Headers", "Content-Type, Authorization, MCP-Session-Id, Last-Event-ID");
    res.setHeader("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
    next();
  });

  app.options("/mcp", (_req, res) => res.status(204).end());

  const rateLimiter = createIpRateLimiter(readIntEnv("MCP_RATE_LIMIT_PER_MINUTE", 180));
  app.use("/mcp", rateLimiter);
  app.use("/mcp", bearerPatAuth);

  app.get("/health", (_req, res) => {
    res.json({
      status: "ok",
      app: "mcp-bridge",
      mode: "streamable-http",
      tools: bridgeConfig.tools.size,
      sessions: activeSessions.size,
    });
  });

  app.post("/mcp", async (req, res) => {
    const sessionId = getSessionId(req);
    const context = buildRequestContext(req, sessionId);

    await runWithRequestContext(context, async () => {
      try {
        let session = sessionId ? activeSessions.get(sessionId) : undefined;

        if (!session) {
          if (sessionId || !isInitializeRequest(req.body)) {
            return jsonRpcError(res, 400, -32000, "Bad Request: no valid MCP session. Send initialize first.");
          }

          const server = createProtocolServer(() => bridgeConfig);
          const transport = new StreamableHTTPServerTransport({
            sessionIdGenerator: () => randomUUID(),
            onsessioninitialized: (newSessionId) => {
              activeSessions.set(newSessionId, { transport, server });
              log("info", `MCP session initialized: ${newSessionId}`);
            },
          });

          transport.onclose = async () => {
            const sid = transport.sessionId;
            if (sid && activeSessions.has(sid)) {
              activeSessions.delete(sid);
              log("info", `MCP session closed: ${sid}`);
            }
            await safeCloseServer(server);
          };

          await server.connect(transport);
          await transport.handleRequest(req, res, req.body);
          return;
        }

        await session.transport.handleRequest(req, res, req.body);
      } catch (e) {
        handleMcpError(res, e);
      }
    });
  });

  app.get("/mcp", async (req, res) => {
    const sessionId = getSessionId(req);
    const context = buildRequestContext(req, sessionId);

    await runWithRequestContext(context, async () => {
      try {
        const session = sessionId ? activeSessions.get(sessionId) : undefined;
        if (!session) {
          return jsonRpcError(res, 400, -32000, "Bad Request: invalid or missing MCP session id.");
        }
        await session.transport.handleRequest(req, res);
      } catch (e) {
        handleMcpError(res, e);
      }
    });
  });

  app.delete("/mcp", async (req, res) => {
    const sessionId = getSessionId(req);
    const context = buildRequestContext(req, sessionId);

    await runWithRequestContext(context, async () => {
      try {
        const session = sessionId ? activeSessions.get(sessionId) : undefined;
        if (!session) {
          return jsonRpcError(res, 400, -32000, "Bad Request: invalid or missing MCP session id.");
        }
        await session.transport.handleRequest(req, res);
      } catch (e) {
        handleMcpError(res, e);
      }
    });
  });

  const port = readIntEnv("MCP_PORT", 5848);
  const host = process.env.MCP_HOST ?? "0.0.0.0";
  const httpServer = app.listen(port, host, () => {
    log("info", `mcp-bridge listening on http://${host}:${port}/mcp. Config: ${configPath}`);
  });

  const shutdown = async () => {
    log("info", "Shutting down…");
    httpServer.close();
    await watcher.close();

    for (const [sessionId, session] of activeSessions) {
      try {
        await session.transport.close();
        await safeCloseServer(session.server);
      } catch (e) {
        log("warn", `Error closing session ${sessionId}: ${e instanceof Error ? e.message : e}`);
      }
    }
    activeSessions.clear();
    process.exit(0);
  };

  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}

async function loadInitialConfig(configPath: string): Promise<BridgeConfig> {
  try {
    const result = await loadConfig(configPath);
    log("info", `Loaded ${result.config.tools.size} tool(s) from ${configPath}`);
    return result.config;
  } catch (e) {
    if (e instanceof ConfigParseError) {
      console.error("FATAL: config load failed.\n");
      console.error(e.message);
    } else if (e instanceof Error) {
      console.error(`FATAL: ${e.message}`);
    } else {
      console.error(`FATAL: ${String(e)}`);
    }
    process.exit(1);
  }
}

function applyLogLevel(config: BridgeConfig) {
  if (config.globalConfig.log_level) {
    setLogLevel(config.globalConfig.log_level);
  }
}

function bearerPatAuth(req: Request, res: Response, next: NextFunction) {
  const token = extractBearerToken(req);
  if (!token || !token.startsWith("edm_pat_")) {
    return jsonRpcError(res, 401, -32001, "Unauthorized: Authorization: Bearer edm_pat_... is required.");
  }
  next();
}

function extractBearerToken(req: Request): string | undefined {
  const authorization = req.header("authorization") ?? "";
  const match = /^Bearer\s+(.+)$/i.exec(authorization.trim());
  return match?.[1]?.trim();
}

function buildRequestContext(req: Request, sessionId?: string) {
  const authorization = req.header("authorization") ?? "";
  const token = extractBearerToken(req) ?? "";
  return {
    userToken: token,
    authorization,
    clientIp: req.ip,
    sessionId,
  };
}

function getSessionId(req: Request): string | undefined {
  const raw = req.header("mcp-session-id");
  return raw?.trim() || undefined;
}

function createIpRateLimiter(maxPerMinute: number) {
  type Bucket = { count: number; resetAt: number };
  const buckets = new Map<string, Bucket>();
  const windowMs = 60_000;

  setInterval(() => {
    const now = Date.now();
    for (const [key, bucket] of buckets) {
      if (bucket.resetAt <= now) buckets.delete(key);
    }
  }, windowMs).unref();

  return (req: Request, res: Response, next: NextFunction) => {
    const now = Date.now();
    const key = req.ip || req.socket.remoteAddress || "unknown";
    let bucket = buckets.get(key);

    if (!bucket || bucket.resetAt <= now) {
      bucket = { count: 0, resetAt: now + windowMs };
      buckets.set(key, bucket);
    }

    bucket.count += 1;
    res.setHeader("X-RateLimit-Limit", String(maxPerMinute));
    res.setHeader("X-RateLimit-Remaining", String(Math.max(0, maxPerMinute - bucket.count)));
    res.setHeader("X-RateLimit-Reset", String(Math.ceil(bucket.resetAt / 1000)));

    if (bucket.count > maxPerMinute) {
      return jsonRpcError(res, 429, -32029, `Rate limit exceeded: ${maxPerMinute} requests/minute per IP.`);
    }

    next();
  };
}

function jsonRpcError(
  res: Response,
  httpStatus: number,
  code: number,
  message: string
) {
  if (res.headersSent) return;
  res.status(httpStatus).json({
    jsonrpc: "2.0",
    error: { code, message },
    id: null,
  });
}

function handleMcpError(res: Response, error: unknown) {
  const message = error instanceof Error ? error.message : String(error);
  log("error", `MCP request failed: ${message}`);
  jsonRpcError(res, 500, -32603, "Internal server error");
}

async function safeCloseServer(server: Server) {
  try {
    await server.close();
  } catch {
    // Non-critical shutdown cleanup.
  }
}

function readIntEnv(name: string, fallback: number): number {
  const raw = process.env[name];
  if (!raw) return fallback;
  const n = Number(raw);
  return Number.isInteger(n) && n > 0 ? n : fallback;
}

function printHelp() {
  console.error(`mcp-bridge — remote Streamable HTTP MCP bridge

Usage:
  mcp-bridge                    Start the HTTP MCP server on /mcp.
                                Loads $MCP_CONFIG (default: ./tools.md).
  mcp-bridge validate [path]    Load and validate the config; exit non-zero if invalid.
  mcp-bridge --help             Show this help.

Environment variables:
  MCP_CONFIG                  Path to tools.md or a directory containing *.md files.
  MCP_HOST                    Host to bind. Default: 0.0.0.0.
  MCP_PORT                    Port to bind. Default: 5848.
  MCP_RATE_LIMIT_PER_MINUTE   Per-IP limit for /mcp. Default: 180.
  EDM_API_URL                 Internal EDM API URL, e.g. http://api:8080.
  ...                         Any vars referenced as \${VAR} in the config file.

Runtime config variables:
  \${request.user_token}      PAT from Authorization: Bearer edm_pat_...
  \${request.authorization}   Full Authorization header.
  \${request.client_ip}       Client IP as seen by Express.
  \${request.session_id}      MCP session id when present.
`);
}

main().catch((e) => {
  console.error("Fatal:", e);
  process.exit(1);
});
