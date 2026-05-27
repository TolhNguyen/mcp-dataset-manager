/**
 * HTTP request executor for a Tool invocation.
 *
 * Responsibilities:
 *   - Validate / coerce incoming arguments against the tool's param schema
 *   - Build URL: base + path-with-{params-interpolated} + query string
 *   - Build body: from body_template (Jinja-style) or auto-built from in:body params
 *   - For multipart: build FormData with file streams
 *   - Apply auth headers
 *   - Execute fetch with timeout
 *   - On 401: invalidate token cache and retry once (for oauth2)
 *   - Apply optional JSONPath response transform
 *   - Truncate response if it exceeds max_response_bytes
 */

import { readFile } from "node:fs/promises";
import path from "node:path";
import { applyAuth, invalidateAuth } from "../auth/index.js";
import { resolveRuntimeTemplates } from "../requestContext.js";
import { renderTemplate } from "./template.js";
import { applyJsonPath } from "../response/transform.js";
import type {
  BridgeConfig,
  ConnectionDef,
  ParamDef,
  ToolDef,
} from "../config/types.js";

export interface ToolCallResult {
  /** Text payload to return to the MCP client. */
  text: string;
  /** Whether the result should be marked isError in the MCP tool response. */
  isError: boolean;
}

export async function executeTool(
  bridgeConfig: BridgeConfig,
  tool: ToolDef,
  rawArgs: Record<string, unknown>
): Promise<ToolCallResult> {
  const connection = bridgeConfig.connections.get(tool.connection);
  if (!connection) {
    return error(`Tool '${tool.name}' references unknown connection '${tool.connection}'.`);
  }

  let coerced: Record<string, unknown>;
  try {
    coerced = coerceParams(tool, rawArgs);
  } catch (e) {
    return error(e instanceof Error ? e.message : String(e));
  }

  try {
    const result = await doRequest(bridgeConfig, connection, tool, coerced, /*isRetry*/ false);
    return result;
  } catch (e) {
    return error(e instanceof Error ? e.message : String(e));
  }
}

// ============================================================
// Argument coercion + validation
// ============================================================

function coerceParams(
  tool: ToolDef,
  raw: Record<string, unknown>
): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  const params = tool.params ?? {};

  for (const [name, def] of Object.entries(params)) {
    let v = raw[name];

    if (v === undefined || v === null || v === "") {
      if (def.default !== undefined) {
        v = def.default;
      } else if (def.required) {
        throw new Error(`Tool '${tool.name}': missing required parameter '${name}'.`);
      } else {
        continue; // optional, no value
      }
    }

    out[name] = coerceValue(name, def, v);

    if (def.enum && !def.enum.includes(out[name])) {
      throw new Error(
        `Tool '${tool.name}': parameter '${name}' must be one of ${JSON.stringify(def.enum)}.`
      );
    }
  }

  // Unknown args are tolerated but logged below. Some LLMs send extras.
  for (const name of Object.keys(raw)) {
    if (!(name in params)) {
      log("debug", `Tool '${tool.name}': dropping unknown argument '${name}'.`);
    }
  }

  return out;
}

function coerceValue(name: string, def: ParamDef, v: unknown): unknown {
  switch (def.type) {
    case "string":
      return String(v);

    case "integer": {
      const n = typeof v === "number" ? v : Number(v);
      if (!Number.isFinite(n) || !Number.isInteger(n)) {
        throw new Error(`Parameter '${name}' must be an integer; got ${JSON.stringify(v)}.`);
      }
      return n;
    }

    case "number": {
      const n = typeof v === "number" ? v : Number(v);
      if (!Number.isFinite(n)) {
        throw new Error(`Parameter '${name}' must be a number; got ${JSON.stringify(v)}.`);
      }
      return n;
    }

    case "boolean":
      if (typeof v === "boolean") return v;
      if (v === "true" || v === 1) return true;
      if (v === "false" || v === 0) return false;
      throw new Error(`Parameter '${name}' must be a boolean; got ${JSON.stringify(v)}.`);

    case "array":
      if (!Array.isArray(v)) {
        throw new Error(`Parameter '${name}' must be an array; got ${JSON.stringify(v)}.`);
      }
      return v;

    case "object":
      if (typeof v !== "object" || Array.isArray(v) || v === null) {
        throw new Error(`Parameter '${name}' must be an object; got ${JSON.stringify(v)}.`);
      }
      return v;

    case "file":
      if (typeof v !== "string") {
        throw new Error(
          `Parameter '${name}' must be a string path to a local file; got ${typeof v}.`
        );
      }
      return v;
  }
}

// ============================================================
// Request building + execution
// ============================================================

async function doRequest(
  bridgeConfig: BridgeConfig,
  connection: ConnectionDef,
  tool: ToolDef,
  params: Record<string, unknown>,
  isRetry: boolean
): Promise<ToolCallResult> {
  // Path interpolation
  let pathStr = tool.path;
  for (const [name, def] of Object.entries(tool.params ?? {})) {
    if (def.in === "path" && params[name] !== undefined) {
      pathStr = pathStr.replace(
        `{${name}}`,
        encodeURIComponent(String(params[name]))
      );
    }
  }

  // Build URL with query params
  const url = new URL(pathStr, ensureTrailingSlash(connection.base_url));
  for (const [name, def] of Object.entries(tool.params ?? {})) {
    if (def.in !== "query") continue;
    const v = params[name];
    if (v === undefined) continue;
    if (Array.isArray(v)) {
      for (const item of v) url.searchParams.append(name, String(item));
    } else {
      url.searchParams.append(name, String(v));
    }
  }

  // Headers
  const headers: Record<string, string> = { Accept: "application/json" };
  for (const [key, value] of Object.entries(connection.default_headers ?? {})) {
    headers[resolveRuntimeTemplates(key)] = resolveRuntimeTemplates(value);
  }

  for (const [name, def] of Object.entries(tool.params ?? {})) {
    if (def.in === "header" && params[name] !== undefined) {
      headers[name] = String(params[name]);
    }
  }

  await applyAuth(connection, headers);

  // Build body
  const contentType = tool.content_type ?? "application/json";
  let body: BodyInit | undefined;
  const method = tool.method;

  if (method !== "GET" && method !== "DELETE") {
    if (contentType.startsWith("multipart/form-data")) {
      const fd = new FormData();
      for (const [name, def] of Object.entries(tool.params ?? {})) {
        if (def.in !== "form") continue;
        const v = params[name];
        if (v === undefined) continue;

        if (def.type === "file") {
          const filePath = String(v);
          const buf = await readFile(filePath);
          const filename = path.basename(filePath);
          fd.append(name, new Blob([buf]), filename);
        } else {
          fd.append(name, String(v));
        }
      }
      body = fd;
      // Don't set Content-Type — fetch sets it with the multipart boundary.
    } else {
      const bodyVars: Record<string, unknown> = {};
      for (const [name, def] of Object.entries(tool.params ?? {})) {
        if (def.in === "body" && params[name] !== undefined) {
          bodyVars[name] = params[name];
        }
      }

      if (tool.body_template) {
        body = renderTemplate(tool.body_template, bodyVars);
        if (!headers["Content-Type"]) headers["Content-Type"] = contentType;
      } else if (Object.keys(bodyVars).length > 0) {
        body = JSON.stringify(bodyVars);
        if (!headers["Content-Type"]) headers["Content-Type"] = contentType;
      }
    }
  }

  // Timeout
  const timeoutMs =
    tool.timeout_ms ??
    connection.timeout_ms ??
    bridgeConfig.globalConfig.default_timeout_ms ??
    30_000;

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);

  let response: Response;
  try {
    log("debug", `→ ${method} ${url.toString()}`);
    response = await fetch(url, {
      method,
      headers,
      body,
      signal: controller.signal,
    });
  } catch (e) {
    if (controller.signal.aborted) {
      return error(`Request timed out after ${timeoutMs}ms: ${method} ${url.toString()}`);
    }
    throw e;
  } finally {
    clearTimeout(timer);
  }

  // Retry once for OAuth2 if the token was rejected.
  if (
    response.status === 401 &&
    !isRetry &&
    connection.auth?.type === "oauth2_client_credentials"
  ) {
    log("info", `Got 401 for ${tool.name}; invalidating OAuth token and retrying once.`);
    invalidateAuth(connection.id);
    return doRequest(bridgeConfig, connection, tool, params, /*isRetry*/ true);
  }

  const responseText = await response.text();
  log("debug", `← ${response.status} (${responseText.length} bytes)`);

  // If the response is JSON, try to parse for optional transform.
  let parsedJson: unknown = undefined;
  const respContentType = response.headers.get("content-type") ?? "";
  if (respContentType.includes("application/json")) {
    try {
      parsedJson = JSON.parse(responseText);
    } catch {
      // Server claimed JSON but returned malformed JSON; pass raw text through.
    }
  }

  // Apply JSONPath transform if present and we have JSON.
  let outputText = responseText;
  if (tool.response_transform && parsedJson !== undefined) {
    try {
      const transformed = applyJsonPath(tool.response_transform, parsedJson);
      outputText = JSON.stringify(transformed, null, 2);
    } catch (e) {
      log("warn", `response_transform failed for ${tool.name}: ${e instanceof Error ? e.message : e}`);
    }
  }

  // Truncate if too big.
  const maxBytes = bridgeConfig.globalConfig.max_response_bytes ?? 0;
  if (maxBytes > 0 && Buffer.byteLength(outputText, "utf8") > maxBytes) {
    const truncated = Buffer.from(outputText, "utf8").subarray(0, maxBytes).toString("utf8");
    outputText =
      truncated +
      `\n\n[... response truncated to ${maxBytes} bytes. Add response_transform to the tool to filter, or raise max_response_bytes in the global config block.]`;
  }

  // Decorate: if there's a response_hint, prepend it so the LLM sees the guidance.
  const isError = !response.ok;
  if (tool.response_hint) {
    outputText = `[hint] ${tool.response_hint}\n\n[status: ${response.status}]\n${outputText}`;
  } else if (isError) {
    outputText = `[status: ${response.status}]\n${outputText}`;
  }

  return { text: outputText, isError };
}

function ensureTrailingSlash(s: string): string {
  return s.endsWith("/") ? s : s + "/";
}

function error(message: string): ToolCallResult {
  return { text: message, isError: true };
}

// ============================================================
// Logging
// ============================================================

type LogLevel = "debug" | "info" | "warn" | "error";

let currentLevel: LogLevel = "info";

export function setLogLevel(level: LogLevel) {
  currentLevel = level;
}

const LEVEL_ORDER: Record<LogLevel, number> = {
  debug: 0,
  info: 1,
  warn: 2,
  error: 3,
};

export function log(level: LogLevel, message: string) {
  if (LEVEL_ORDER[level] < LEVEL_ORDER[currentLevel]) return;
  // stderr — stdout is reserved for the MCP protocol.
  const stamp = new Date().toISOString();
  console.error(`[${stamp}] [${level}] ${message}`);
}
