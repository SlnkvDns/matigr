import { createHttpClient } from '../../shared/api/http';
import { readStandings } from './standings';
import type { Standings } from './standings';

type Connection = 'connecting' | 'live' | 'degraded';
type State = Readonly<{ snapshot: Standings | undefined; error: unknown; loading: boolean; connection: Connection }>;
export type SnapshotSubscription = (tournament: string, version: (value: string) => void, state: (live: boolean) => void) => () => void;
const versionPattern = /^(0|[1-9][0-9]*)$/;
export const subscribeSnapshots: SnapshotSubscription = (tournament, version, state) => {
  if (typeof EventSource === 'undefined') { state(false); return () => {}; }
  // Anonymous stream: no bearer or session-derived fields; withCredentials is false.
  const source = new EventSource(`/api/v1/results/tournaments/${encodeURIComponent(tournament)}/standings/events`, { withCredentials: false });
  source.onopen = () => state(true);
  source.onerror = () => state(false);
  source.addEventListener('snapshot', event => {
    try {
      const value: unknown = JSON.parse((event as MessageEvent<string>).data);
      if (!value || typeof value !== 'object') throw new Error();
      const data = value as Record<string, unknown>;
      if (Object.keys(data).length !== 2 || data.tournamentId !== tournament || typeof data.snapshotVersion !== 'string' || !versionPattern.test(data.snapshotVersion)) throw new Error();
      version(data.snapshotVersion);
    } catch { state(false); }
  });
  return () => { source.close(); source.onopen = null; source.onerror = null; };
};

export class StandingsController {
  #state: State = Object.freeze({ snapshot: undefined, error: undefined, loading: false, connection: 'connecting' });
  #listeners = new Set<() => void>();
  #generation = 0;
  #active = false;
  #inFlight = false;
  #queued = false;
  #wanted = 0n;
  #live: boolean | undefined;
  #cancel: AbortController | undefined;
  #unsubscribe: (() => void) | undefined;
  #periodic: ReturnType<typeof setInterval> | undefined;
  #retry: ReturnType<typeof setTimeout> | undefined;
  constructor(readonly tournament: string,
    private readonly read: (signal: AbortSignal) => Promise<unknown> = async signal => (await createHttpClient('/api/v1/results').get<unknown>(`/tournaments/${encodeURIComponent(tournament)}/standings`, { signal })).data,
    private readonly events: SnapshotSubscription = subscribeSnapshots,
    private readonly reconcileMilliseconds = 10000) {
    if (!Number.isFinite(reconcileMilliseconds) || reconcileMilliseconds < 1) throw new Error('Invalid reconciliation interval');
  }
  getSnapshot = () => this.#state;
  subscribe = (listener: () => void) => { this.#listeners.add(listener); return () => { this.#listeners.delete(listener); }; };
  #publish(update: Partial<State>) { this.#state = Object.freeze({ ...this.#state, ...update }); for (const listener of this.#listeners) listener(); }
  start() {
    this.stop(); if (!this.tournament) return;
    const generation = ++this.#generation; this.#active = true; this.#inFlight = false; this.#queued = false; this.#live = undefined;
    // Register the stream before GET; open/reconnect always triggers a fresh GET as well.
    this.#unsubscribe = this.events(this.tournament, value => {
      if (!this.#active || generation !== this.#generation || !versionPattern.test(value)) return;
      if (BigInt(value) > this.#wanted) this.#wanted = BigInt(value);
      if (this.#wanted > BigInt(this.#state.snapshot?.snapshotVersion ?? '0')) void this.refresh();
    }, live => {
      if (!this.#active || generation !== this.#generation) return;
      this.#live = live; this.#publish({ connection: live ? 'live' : 'degraded' });
      if (live) void this.refresh();
    });
    this.#periodic = setInterval(() => { void this.refresh(); }, this.reconcileMilliseconds);
    void this.refresh();
  }
  refresh = async () => {
    if (!this.#active) return;
    if (this.#inFlight) { this.#queued = true; return; }
    const generation = this.#generation; this.#inFlight = true; this.#queued = false;
    const cancellation = new AbortController(); this.#cancel = cancellation;
    this.#publish({ loading: true });
    try {
      const value = readStandings(await this.read(cancellation.signal), this.tournament);
      if (!this.#active || generation !== this.#generation || cancellation.signal.aborted) return;
      const current = BigInt(this.#state.snapshot?.snapshotVersion ?? '0');
      if (BigInt(value.snapshotVersion) >= current) this.#publish({ snapshot: value, error: undefined, connection: this.#live === undefined ? 'connecting' : this.#live ? 'live' : 'degraded' });
      // An event may have arrived during GET. Retry after a bounded technical delay, never hot-loop a stale replica.
      if (BigInt(value.snapshotVersion) < this.#wanted) {
        clearTimeout(this.#retry); this.#retry = setTimeout(() => { void this.refresh(); }, Math.min(1000, this.reconcileMilliseconds));
      }
    } catch (error) {
      if (this.#active && generation === this.#generation && !cancellation.signal.aborted) this.#publish({ error, connection: 'degraded' });
    } finally {
      if (this.#active && generation === this.#generation) {
        this.#inFlight = false; this.#publish({ loading: false });
        if (this.#queued) { this.#queued = false; void this.refresh(); }
      }
    }
  };
  stop() {
    this.#active = false; ++this.#generation; this.#cancel?.abort(); this.#unsubscribe?.();
    clearInterval(this.#periodic); clearTimeout(this.#retry); this.#unsubscribe = undefined;
  }
}
