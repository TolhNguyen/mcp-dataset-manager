/**
 * Domain types for the MCP bridge configuration model.
 *
 * Configs are loaded from one or more Markdown files; each fenced ```yaml block
 * with a `type:` field is parsed into one of these shapes.
 */

export type ParamLocation = "path" | "query" | "body" | "form" | "header";
export type ParamType =
  | "string"
  | "integer"
  | "number"
  | "boolean"
  | "array"
  | "object"
  | "file";

export interface ParamDef {
  in: ParamLocation;
  type: ParamType;
  required?: boolean;
  default?: unknown;
  enum?: unknown[];
  description?: string;
  /** For arrays: element type (defaults to string). */
  items?: { type: Exclude<ParamType, "array" | "object" | "file"> };
}

export type AuthDef =
  | { type: "none" }
  | { type: "header"; header: string; value: string }
  | { type: "bearer"; token: string }
  | { type: "basic"; username: string; password: string }
  | {
      type: "oauth2_client_credentials";
      token_url: string;
      client_id: string;
      client_secret: string;
      scope?: string;
      /** Where to send client credentials: "body" (default) or "basic". */
      client_auth?: "body" | "basic";
    };

export interface ConnectionDef {
  type: "connection";
  id: string;
  base_url: string;
  auth?: AuthDef;
  default_headers?: Record<string, string>;
  timeout_ms?: number;
}

export interface ToolDef {
  type: "tool";
  name: string;
  description: string;
  connection: string;
  method: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
  path: string;
  content_type?: string;
  params?: Record<string, ParamDef>;
  body_template?: string;
  /** JSONPath expression starting with `$`. */
  response_transform?: string;
  /** Free text hint for the LLM about how to interpret the response. */
  response_hint?: string;
  timeout_ms?: number;
}

export interface GlobalConfigDef {
  type: "config";
  log_level?: "debug" | "info" | "warn" | "error";
  default_timeout_ms?: number;
  /** Maximum response payload (in bytes) to return verbatim. 0 = unlimited. */
  max_response_bytes?: number;
}

export interface BridgeConfig {
  globalConfig: GlobalConfigDef;
  connections: Map<string, ConnectionDef>;
  tools: Map<string, ToolDef>;
}

/** Metadata about where a block was located in the source MD, for error messages. */
export interface BlockSource {
  file: string;
  line: number;
  blockIndex: number;
}
