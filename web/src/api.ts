export type User = { email: string; role: 'staff' | 'customer' };
export type Session = { user: User | null; csrfToken: string };
export type Product = { sku: string; name: string; unitPriceMinor: number; active: boolean; available: number };
export type Receipt = { sku: string; quantity: number; reason: string; requestKey: string };

export class ApiError extends Error {
  constructor(readonly status: number, message: string, readonly code?: string) {
    super(message);
  }
}

export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'The request could not be completed.';
}

export async function request<T = void>(
  path: string,
  options?: { method: 'POST' | 'PUT'; csrfToken: string; body?: unknown },
): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (options) headers['X-CSRF-TOKEN'] = options.csrfToken;
  if (options?.body !== undefined) headers['Content-Type'] = 'application/json';
  let response: Response;
  try {
    response = await fetch(path, {
      method: options?.method ?? 'GET',
      credentials: 'same-origin',
      cache: 'no-store',
      headers,
      body: options?.body === undefined ? undefined : JSON.stringify(options.body),
    });
  } catch {
    throw new Error('Could not reach the retail app. Check your connection and try again.');
  }
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    const validation = problem?.errors ? Object.values(problem.errors).flat().join(' ') : '';
    const fallback = response.status === 401 ? 'Sign in with your demo email and password.' : 'Request failed (' + response.status + ').';
    const message = validation || problem?.detail || problem?.error || problem?.message || problem?.title || fallback;
    if (response.status === 401 && path !== '/api/login') window.dispatchEvent(new Event('session-expired'));
    throw new ApiError(response.status, typeof message === 'string' ? message : fallback, problem?.code);
  }
  const content = await response.text();
  return content ? JSON.parse(content) as T : undefined as T;
}
