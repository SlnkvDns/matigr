import type { ProblemDetails } from './types';

export type ApiBasePath = '/api/v1/checking' | '/api/v1/results' | '/api/v1/appeals' | '/dev/v1';

interface RequestOptions {
  accessToken?: string;
  signal?: AbortSignal;
}

interface PostOptions extends RequestOptions {
  idempotencyKey?: string;
  ifMatch?: string;
}

export interface HttpResponse<T> {
  status: number;
  data: T | undefined;
  etag: string | null;
}

export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails | undefined;

  constructor(status: number, problem?: ProblemDetails) {
    // Do not put raw response text, URLs, tokens or server detail in the message.
    super(`HTTP request failed (${status})`);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
  }
}

export class ApiNetworkError extends Error {
  constructor() {
    super('The server could not be reached. The outcome of a submitted command may be unknown.');
    this.name = 'ApiNetworkError';
  }
}

export class ApiProtocolError extends Error {
  constructor() {
    super('The server returned an unexpected response format.');
    this.name = 'ApiProtocolError';
  }
}

function readProblem(value: unknown, status: number): ProblemDetails | undefined {
  if (!value || typeof value !== 'object') return undefined;
  const candidate = value as Record<string, unknown>;
  if (candidate.status !== status || typeof candidate.type !== 'string' || typeof candidate.title !== 'string'
    || typeof candidate.code !== 'string' || !candidate.code || typeof candidate.correlationId !== 'string' || !candidate.correlationId) return undefined;
  // Copy the known diagnostic fields only. No raw body, detail or instance URL.
  return { type: candidate.type, title: candidate.title, status,
    code: candidate.code, correlationId: candidate.correlationId };
}

function relativeUrl(basePath: ApiBasePath, path: string): string {
  if (path !== '' && (!path.startsWith('/') || path.startsWith('//')))
    throw new TypeError('API endpoint must be a relative path');
  const base = 'https://matigr.invalid';
  const url = new URL(basePath + path, base);
  if (url.origin !== base || (url.pathname !== basePath && !url.pathname.startsWith(`${basePath}/`)) || url.hash)
    throw new TypeError('API endpoint is outside the selected service');
  return url.pathname + url.search;
}

function isCancelled(error: unknown, signal?: AbortSignal): boolean {
  return signal?.aborted === true || (typeof error === 'object' && error !== null && 'name' in error && error.name === 'AbortError');
}

export function createHttpClient(basePath: ApiBasePath, fetchImpl: typeof fetch = globalThis.fetch) {
  async function request<T>(method: 'GET' | 'POST', path: string, body: unknown, options: PostOptions, text = false): Promise<HttpResponse<T>> {
    const url = relativeUrl(basePath, path);
    const headers = new Headers({ Accept: 'application/json, application/problem+json' });
    if (options.accessToken) headers.set('Authorization', `Bearer ${options.accessToken}`);
    if (options.idempotencyKey !== undefined) {
      if (!options.idempotencyKey.trim()) throw new TypeError('Idempotency key must be present');
      headers.set('Idempotency-Key', options.idempotencyKey);
    }
    if (options.ifMatch !== undefined) headers.set('If-Match', options.ifMatch);
    const init: RequestInit = { method, headers, credentials: 'omit', cache: 'no-store', redirect: 'error' };
    if (body !== undefined) {
      headers.set('Content-Type', 'application/json');
      init.body = JSON.stringify(body);
    }
    if (options.signal) init.signal = options.signal;

    let response: Response;
    try {
      response = await fetchImpl(url, init);
    } catch (error) {
      if (isCancelled(error, options.signal)) throw error;
      throw new ApiNetworkError();
    }
    const contentType = response.headers.get('Content-Type')?.split(';')[0]?.trim().toLowerCase();
    if (!response.ok) {
      let problem: ProblemDetails | undefined;
      if (contentType === 'application/problem+json') {
        try { problem = readProblem(await response.json(), response.status); }
        catch (error) {
          if (isCancelled(error, options.signal)) throw error;
          // An invalid error body must not hide the actual HTTP status.
        }
      }
      throw new ApiError(response.status, problem);
    }
    const etag = response.headers.get('ETag');
    if (response.status === 204) return { status: 204, data: undefined, etag };
    if (text) {
      if (contentType !== 'text/plain') throw new ApiProtocolError();
      return { status: response.status, data: await response.text() as T, etag };
    }
    if (contentType !== 'application/json') throw new ApiProtocolError();
    let data: T;
    try { data = await response.json() as T; }
    catch (error) {
      if (isCancelled(error, options.signal)) throw error;
      throw new ApiProtocolError();
    }
    return { status: response.status, data, etag };
  }

  return {
    text(path: string, options: RequestOptions = {}) { return request<string>('GET', path, undefined, options, true); },
    get<T>(path: string, options: RequestOptions = {}) { return request<T>('GET', path, undefined, options); },
    // For stateless technical POSTs such as dev credential issuance.
    post<T>(path: string, body: unknown, options: RequestOptions = {}) { return request<T>('POST', path, body, options); },
    command<T>(path: string, body: unknown, options: PostOptions & { idempotencyKey: string }) {
      return request<T>('POST', path, body, options);
    },
  };
}
