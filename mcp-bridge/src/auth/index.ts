/**
 * Authentication strategies for HTTP connections.
 *
 * Each strategy mutates the outbound headers (and, for oauth2, may make an
 * upstream call to fetch a token). Tokens are cached per-connection until
 * they expire, with a 30s safety buffer.
 */

import type { AuthDef, ConnectionDef } from "../config/types.js";
import { resolveRuntimeTemplates } from "../requestContext.js";

interface CachedToken {
  accessToken: string;
  tokenType: string;
  expiresAt: number; // epoch ms
}

const tokenCache = new Map<string, CachedToken>();
const TOKEN_BUFFER_MS = 30_000;

export async function applyAuth(
  connection: ConnectionDef,
  headers: Record<string, string>
): Promise<void> {
  const auth = connection.auth;
  if (!auth || auth.type === "none") return;

  switch (auth.type) {
    case "header":
      headers[resolveRuntimeTemplates(auth.header)] = resolveRuntimeTemplates(auth.value);
      return;

    case "bearer":
      headers["Authorization"] = `Bearer ${resolveRuntimeTemplates(auth.token)}`;
      return;

    case "basic": {
      const username = resolveRuntimeTemplates(auth.username);
      const password = resolveRuntimeTemplates(auth.password);
      const b64 = Buffer.from(`${username}:${password}`).toString("base64");
      headers["Authorization"] = `Basic ${b64}`;
      return;
    }

    case "oauth2_client_credentials": {
      const token = await getOauth2Token(connection.id, auth);
      headers["Authorization"] = `${token.tokenType} ${token.accessToken}`;
      return;
    }
  }
}

/** Invalidate cached credentials for a connection (call this on 401 responses). */
export function invalidateAuth(connectionId: string): void {
  tokenCache.delete(connectionId);
}

async function getOauth2Token(
  connectionId: string,
  auth: Extract<AuthDef, { type: "oauth2_client_credentials" }>
): Promise<CachedToken> {
  const cached = tokenCache.get(connectionId);
  if (cached && cached.expiresAt > Date.now() + TOKEN_BUFFER_MS) {
    return cached;
  }

  const body = new URLSearchParams();
  body.set("grant_type", "client_credentials");
  if (auth.scope) body.set("scope", resolveRuntimeTemplates(auth.scope));

  const headers: Record<string, string> = {
    "Content-Type": "application/x-www-form-urlencoded",
    Accept: "application/json",
  };

  const clientAuth = auth.client_auth ?? "body";
  if (clientAuth === "basic") {
    const clientId = resolveRuntimeTemplates(auth.client_id);
    const clientSecret = resolveRuntimeTemplates(auth.client_secret);
    const b64 = Buffer.from(`${clientId}:${clientSecret}`).toString("base64");
    headers["Authorization"] = `Basic ${b64}`;
  } else {
    body.set("client_id", resolveRuntimeTemplates(auth.client_id));
    body.set("client_secret", resolveRuntimeTemplates(auth.client_secret));
  }

  const response = await fetch(resolveRuntimeTemplates(auth.token_url), {
    method: "POST",
    headers,
    body: body.toString(),
  });

  const text = await response.text();
  if (!response.ok) {
    throw new Error(
      `OAuth2 token request failed (${response.status}): ${text.slice(0, 500)}`
    );
  }

  let parsed: {
    access_token?: string;
    token_type?: string;
    expires_in?: number;
  };
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new Error(`OAuth2 token endpoint returned non-JSON: ${text.slice(0, 200)}`);
  }

  if (!parsed.access_token) {
    throw new Error(
      `OAuth2 token response missing access_token: ${text.slice(0, 500)}`
    );
  }

  const expiresIn = parsed.expires_in ?? 3600; // default 1h if not specified
  const cached_new: CachedToken = {
    accessToken: parsed.access_token,
    tokenType: parsed.token_type ?? "Bearer",
    expiresAt: Date.now() + expiresIn * 1000,
  };
  tokenCache.set(connectionId, cached_new);
  return cached_new;
}
