/**
 * Recursively replaces ${VAR} placeholders in strings using process.env.
 *
 * Rules:
 *   ${VAR}          → process.env.VAR (or throws if missing)
 *   ${VAR:-default} → process.env.VAR, falling back to "default" if unset/empty
 *   $$              → literal "$" (escape)
 *
 * Walks objects/arrays. Non-string values pass through unchanged.
 */

export function expandEnv<T>(value: T): T {
  return walk(value) as T;
}

function walk(value: unknown): unknown {
  if (typeof value === "string") {
    return expandString(value);
  }
  if (Array.isArray(value)) {
    return value.map(walk);
  }
  if (value !== null && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value)) {
      out[k] = walk(v);
    }
    return out;
  }
  return value;
}

function expandString(s: string): string {
  // Replace $$ with a placeholder we'll swap back at the end.
  const ESC = "\u0000ESC$\u0000";
  let work = s.replace(/\$\$/g, ESC);

  work = work.replace(
    /\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}/g,
    (_match, name: string, fallback: string | undefined) => {
      const v = process.env[name];
      if (v !== undefined && v !== "") return v;
      if (fallback !== undefined) return fallback;
      throw new EnvVarMissingError(name);
    }
  );

  return work.replace(new RegExp(ESC, "g"), "$");
}

export class EnvVarMissingError extends Error {
  constructor(public readonly name: string) {
    super(
      `Environment variable '${name}' is referenced in the config but not set. ` +
        `Either export it before starting the bridge, set it in your Claude Desktop "env" block, ` +
        `or provide it in a .env file beside the config.`
    );
    this.name = "EnvVarMissingError";
  }
}
