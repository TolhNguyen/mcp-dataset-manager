import { AsyncLocalStorage } from "node:async_hooks";

export interface RequestContext {
  userToken: string;
  authorization: string;
  clientIp?: string;
  sessionId?: string;
}

const storage = new AsyncLocalStorage<RequestContext>();

export function runWithRequestContext<T>(
  context: RequestContext,
  callback: () => T | Promise<T>
): T | Promise<T> {
  return storage.run(context, callback);
}

export function getRequestContext(): RequestContext | undefined {
  return storage.getStore();
}

export function resolveRuntimeTemplates(value: string): string {
  return value.replace(/\$\{request\.([a-zA-Z_][a-zA-Z0-9_]*)\}/g, (_match, key: string) => {
    const context = getRequestContext();
    if (!context) {
      throw new Error(
        `Runtime request variable request.${key} was used outside an HTTP request context.`
      );
    }

    const lookup: Record<string, string | undefined> = {
      user_token: context.userToken,
      authorization: context.authorization,
      client_ip: context.clientIp,
      session_id: context.sessionId,
    };

    const resolved = lookup[key];
    if (resolved === undefined || resolved === "") {
      throw new Error(`Runtime request variable request.${key} is not available.`);
    }

    return resolved;
  });
}
