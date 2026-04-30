// In dev, Vite proxies /api and /hubs to localhost:8080.
// In production (Docker), nginx proxies them from the same origin.
export const BASE_URL = import.meta.env.VITE_API_URL ?? '';
export const HUB_URL = `${BASE_URL}/hubs/chat`;

export async function apiFetch<T = unknown>(
  path: string,
  options: RequestInit = {},
  token?: string | null,
): Promise<T> {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...(options.headers as Record<string, string> | undefined),
  };
  if (token) headers['Authorization'] = `Bearer ${token}`;

  const res = await fetch(`${BASE_URL}${path}`, { ...options, headers });
  if (!res.ok) {
    let msg = res.statusText;
    try { msg = await res.text(); } catch { /* ignore */ }
    throw new Error(msg || String(res.status));
  }
  if (res.status === 204) return undefined as T;
  return res.json() as Promise<T>;
}
