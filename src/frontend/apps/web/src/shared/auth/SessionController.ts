import type { IdentityProvider, UserProfile } from './identity';
import { PrivateWorkspace } from './PrivateWorkspace';

export type SessionSnapshot = Readonly<{
  revision: number;
  status: 'anonymous' | 'selecting' | 'authenticated';
  profile?: UserProfile;
  error?: unknown;
  notice?: 'expired' | 'unauthorized';
}>;

export class SessionController {
  #snapshot: SessionSnapshot = Object.freeze({ revision: 0, status: 'anonymous' });
  #listeners = new Set<() => void>();
  #selection: AbortController | undefined;
  #workspace: PrivateWorkspace | undefined;
  #expiry: ReturnType<typeof setTimeout> | undefined;
  constructor(readonly provider?: IdentityProvider) {}

  getSnapshot = () => this.#snapshot;
  subscribe = (listener: () => void) => {
    this.#listeners.add(listener);
    return () => { this.#listeners.delete(listener); };
  };
  workspace(): PrivateWorkspace {
    if (!this.#workspace) throw new DOMException('An authenticated session is required', 'AbortError');
    return this.#workspace;
  }

  #publish(snapshot: SessionSnapshot) {
    this.#snapshot = Object.freeze(snapshot);
    for (const listener of this.#listeners) listener();
  }
  #clear() {
    clearTimeout(this.#expiry);
    this.#expiry = undefined;
    this.#workspace?.dispose();
    this.#workspace = undefined;
    this.#selection?.abort();
    this.#selection = undefined;
  }
  signOut = (notice?: 'expired' | 'unauthorized') => {
    this.#clear();
    this.#publish({ revision: this.#snapshot.revision + 1, status: 'anonymous', ...(notice ? { notice } : {}) });
  };

  async select(key: string) {
    if (!this.provider) return;
    this.#clear();
    const revision = this.#snapshot.revision + 1;
    const selection = new AbortController();
    this.#selection = selection;
    this.#publish({ revision, status: 'selecting' });
    try {
      const credential = await this.provider.select(key, selection.signal);
      if (this.#snapshot.revision !== revision || selection.signal.aborted) return;
      this.#selection = undefined;
      this.#workspace = new PrivateWorkspace(credential.accessToken, () => {
        if (this.#snapshot.revision === revision) this.signOut('unauthorized');
      });
      this.#publish({ revision, status: 'authenticated', profile: credential.profile });
      const expire = () => {
        if (this.#snapshot.revision !== revision) return;
        const remaining = credential.expiresAt - Date.now();
        if (remaining <= 0) this.signOut('expired');
        else this.#expiry = setTimeout(expire, Math.min(remaining, 2_147_483_647));
      };
      expire();
    } catch (error) {
      if (this.#snapshot.revision !== revision || selection.signal.aborted) return;
      this.#selection = undefined;
      this.#publish({ revision, status: 'anonymous', error });
    }
  }
}
