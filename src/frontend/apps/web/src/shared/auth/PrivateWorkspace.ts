import { ApiError, createHttpClient } from '../api/http';
import type { ApiBasePath } from '../api/http';

type UserApi = Exclude<ApiBasePath, '/dev/v1'>;
interface ReadOptions { signal?: AbortSignal }
interface CommandOptions extends ReadOptions { idempotencyKey: string; ifMatch?: string }

// One workspace per credential, including a new selection of the same subject.
// Feature state belongs inside PrivateSessionBoundary; cached data/URLs belong here.
export class PrivateWorkspace {
  #token: string | undefined;
  #active = true;
  #requests = new AbortController();
  #cache = new Map<string, unknown>();
  #materialUrls = new Set<string>();
  readonly signal = this.#requests.signal;

  constructor(token: string, private readonly onUnauthorized: () => void) { this.#token = token; }

  assertActive() {
    if (!this.#active) throw new DOMException('Session changed', 'AbortError');
  }
  getCached<T>(key: string): T | undefined { this.assertActive(); return this.#cache.get(key) as T | undefined; }
  cache(key: string, value: unknown) { this.assertActive(); this.#cache.set(key, value); }
  materialUrl(blob: Blob): string {
    this.assertActive();
    const url = URL.createObjectURL(blob);
    this.#materialUrls.add(url);
    return url;
  }
  releaseMaterial(url: string) {
    if (this.#materialUrls.delete(url)) URL.revokeObjectURL(url);
  }

  client(basePath: UserApi) {
    const http = createHttpClient(basePath);
    const run = async <T>(request: (options: { accessToken: string; signal: AbortSignal }) => Promise<T>, signal?: AbortSignal) => {
      this.assertActive();
      const combined = signal ? AbortSignal.any([this.signal, signal]) : this.signal;
      try {
        const result = await request({ accessToken: this.#token!, signal: combined });
        this.assertActive(); // Also blocks late fetch/body responses that ignored abort.
        if (combined.aborted) throw new DOMException('Request cancelled', 'AbortError');
        return result;
      } catch (error) {
        this.assertActive();
        if (combined.aborted) throw new DOMException('Request cancelled', 'AbortError');
        if (error instanceof ApiError && error.status === 401) this.onUnauthorized();
        throw error;
      }
    };
    return {
      text: (path: string, options: ReadOptions = {}) => run(auth => http.text(path, auth), options.signal),
      get: <T>(path: string, options: ReadOptions = {}) => run(auth => http.get<T>(path, auth), options.signal),
      command: <T>(path: string, body: unknown, options: CommandOptions) =>
        run(auth => http.command<T>(path, body, { ...options, ...auth }), options.signal),
    };
  }

  dispose() {
    this.#active = false;
    this.#token = undefined;
    this.#cache.clear();
    for (const url of this.#materialUrls) URL.revokeObjectURL(url);
    this.#materialUrls.clear();
    this.#requests.abort();
  }
}
