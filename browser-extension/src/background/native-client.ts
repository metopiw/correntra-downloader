import {
  buildNativeRequest,
  fitsNativeMessageLimit,
  NATIVE_HOST_NAME,
  parseNativeResponse,
  type NativeRequestKind,
  type NativeResponse
} from "../shared/protocol";

export interface NativeResult {
  available: boolean;
  accepted: boolean;
  response?: NativeResponse;
}

export function sendNativeRequest(
  kind: NativeRequestKind,
  payload: Record<string, unknown>,
  timeoutMs: number
): Promise<NativeResult> {
  const request = buildNativeRequest(kind, payload);
  if (!fitsNativeMessageLimit(request)) {
    return Promise.resolve({ available: false, accepted: false });
  }

  return new Promise((resolve) => {
    let settled = false;
    const finish = (result: NativeResult): void => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(timer);
      resolve(result);
    };
    const timer = setTimeout(() => finish({ available: false, accepted: false }), timeoutMs);

    try {
      chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, request, (rawResponse: unknown) => {
        if (chrome.runtime.lastError) {
          console.warn("Correntra Native Messaging error:", chrome.runtime.lastError.message);
          finish({ available: false, accepted: false });
          return;
        }
        const response = parseNativeResponse(rawResponse, request.requestId);
        if (!response) {
          finish({ available: true, accepted: false });
          return;
        }
        finish({ available: true, accepted: response.payload.accepted, response });
      });
    } catch {
      finish({ available: false, accepted: false });
    }
  });
}

function cookiesForUrl(url: string): Promise<chrome.cookies.Cookie[]> {
  return new Promise((resolve) => {
    try {
      chrome.cookies.getAll({ url }, (cookies) => {
        if (chrome.runtime.lastError) {
          resolve([]);
          return;
        }
        resolve(cookies.slice(0, 80));
      });
    } catch {
      resolve([]);
    }
  });
}

export async function createEphemeralAuthContext(
  url: string,
  referrer: string | undefined,
  includeSession: boolean
): Promise<Record<string, unknown> | undefined> {
  if (!includeSession) {
    return undefined;
  }
  const hasCookies = await chrome.permissions.contains({ permissions: ["cookies"] });
  if (!hasCookies) {
    return undefined;
  }

  const cookies = await cookiesForUrl(url);
  const cookieHeader = cookies.map((cookie) => `${cookie.name}=${cookie.value}`).join("; ").slice(0, 32_768);
  const headers: Record<string, string> = {
    "User-Agent": navigator.userAgent.slice(0, 512)
  };
  if (cookieHeader) {
    headers.Cookie = cookieHeader;
  }
  if (referrer) {
    headers.Referer = referrer.slice(0, 16_384);
  }
  return { ephemeral: true, headers };
}
