/**
 * Tiny Jinja-style template engine for body_template.
 *
 * Supported:
 *   {{ var }}                   value substitution
 *   {{ var | json }}            JSON.stringify the value
 *   {{ var | default 'x' }}     fallback when var is null/undefined
 *   {{ var | default "x" }}     ...double quotes also work
 *   {{ var | upper }}           uppercase string
 *   {{ var | lower }}           lowercase string
 *   {{ var | urlencode }}       encodeURIComponent
 *
 * Filters can be chained: {{ var | default 'x' | upper }}
 * Variables are resolved against a flat record (params).
 * Missing variables raise a TemplateError unless filtered with `default`.
 */

export interface TemplateError {
  template: string;
  message: string;
}

const FILTERS: Record<string, (value: unknown, arg?: string) => unknown> = {
  json: (v) => JSON.stringify(v),
  default: (v, arg) => (v === undefined || v === null ? arg : v),
  upper: (v) => String(v ?? "").toUpperCase(),
  lower: (v) => String(v ?? "").toLowerCase(),
  urlencode: (v) => encodeURIComponent(String(v ?? "")),
};

export function renderTemplate(
  template: string,
  vars: Record<string, unknown>
): string {
  return template.replace(/\{\{([^}]+)\}\}/g, (match, expr: string) => {
    const segments = expr.split("|").map((s) => s.trim());
    const varName = segments.shift();
    if (!varName) {
      throw new Error(`Empty template expression: ${match}`);
    }

    let value: unknown = vars[varName];

    for (const filter of segments) {
      const { name, arg } = parseFilter(filter, match);
      const fn = FILTERS[name];
      if (!fn) {
        throw new Error(`Unknown filter '${name}' in template expression ${match}`);
      }
      value = fn(value, arg);
    }

    if (value === undefined || value === null) {
      throw new Error(
        `Template variable '${varName}' is undefined and has no default. Expression: ${match}`
      );
    }

    return String(value);
  });
}

function parseFilter(filter: string, original: string): { name: string; arg?: string } {
  // "default 'x'", "default \"x\"", "json", "upper"
  const trimmed = filter.trim();
  const spaceIdx = trimmed.indexOf(" ");
  if (spaceIdx < 0) {
    return { name: trimmed };
  }

  const name = trimmed.slice(0, spaceIdx).trim();
  const rest = trimmed.slice(spaceIdx + 1).trim();

  // Quoted string arg.
  const quoted = /^(['"])(.*)\1$/.exec(rest);
  if (quoted) {
    return { name, arg: quoted[2] };
  }

  // Numeric or bareword arg.
  return { name, arg: rest };
}
