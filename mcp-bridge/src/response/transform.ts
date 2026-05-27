/**
 * Optional response transformation via JSONPath.
 *
 * Tools can declare `response_transform: "$.foo[*].bar"` to filter the JSON
 * payload before returning to the LLM. Useful for trimming large responses
 * down to the fields the LLM actually needs.
 *
 * Implementation note: we use jsonpath-plus which returns an array of matches.
 * For a single-value path we unwrap the singleton, but for multi-match paths
 * the array shape is preserved.
 */

import { JSONPath } from "jsonpath-plus";

export function applyJsonPath(expression: string, data: unknown): unknown {
  const expr = expression.trim();
  if (!expr.startsWith("$")) {
    throw new Error(
      `response_transform must be a JSONPath expression starting with '$'. Got: ${expr}`
    );
  }

  const result = JSONPath({
    path: expr,
    json: data as object,
    wrap: true, // always return an array
  });

  // If the path returned exactly one match and the expression isn't obviously
  // a multi-match (no wildcards or filters), unwrap it. Otherwise keep the array.
  if (Array.isArray(result) && result.length === 1 && !looksLikeMultiMatch(expr)) {
    return result[0];
  }
  return result;
}

function looksLikeMultiMatch(expr: string): boolean {
  return /[\*?@\[][^]]*[\]?]|\.\.|\?\(/.test(expr);
}
