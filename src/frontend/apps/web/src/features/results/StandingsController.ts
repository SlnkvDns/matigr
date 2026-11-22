import { createHttpClient } from '../../shared/api/http';
import { readStandings } from './standings';
import type { Standings } from './standings';

type Connection = 'connecting' | 'live' | 'degraded';
type State = Readonly<{ snapshot: Standings | undefined; error: unknown; loading: boolean; connection: Connection }>;
export type SnapshotSubscription = (tournament: string, snapshot: (value: unknown) => void, disconnected: () => void) => () => void;

export const subscribeSnapshots: SnapshotSubscription = (tournament, snapshot, disconnected) => {
  if (typeof WebSocket === 'undefined') { disconnected(); return () => {}; }
  const url = new URL(`/api/v1/results/tournaments/${encodeURIComponent(tournament)}/standings/events`, window.location.href);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  // This is the public feed. The browser sends no session bearer or private payload.
  const socket = new WebSocket(url);
  function close() {
    clearTimeout(handshake);
    socket.onmessage = socket.onerror = socket.onclose = null;
    socket.close();
  }
  function fail() { close(); disconnected(); }
  const handshake = setTimeout(fail, 10_000);
  socket.onerror = socket.onclose = fail;
  socket.onmessage = event => {
    try {
      snapshot(JSON.parse(event.data as string));
      clearTimeout(handshake);
    } catch { fail(); }
  };
  return close;
};

export class StandingsController {
  #state: State = Object.freeze({ snapshot: undefined, error: undefined, loading: false, connection: 'connecting' });
  #listeners = new Set<() => void>();
  #generation = 0;
  #active = false;
  #cancel: AbortController | undefined;
  #unsubscribe: (() => void) | undefined;
  #retry: ReturnType<typeof setTimeout> | undefined;
  #failedConnections = 0;
  constructor(readonly tournament: string,
    private readonly read: (signal: AbortSignal, currentVersion?: string) => Promise<unknown> = async (signal, currentVersion) => {
      const response = await createHttpClient('/api/v1/results').get<unknown>(`/tournaments/${encodeURIComponent(tournament)}/standings`, {
        signal, ...(currentVersion === undefined ? {} : { ifNoneMatch: `"${currentVersion}"` }),
      });
      return response.data;
    },
    private readonly events: SnapshotSubscription = subscribeSnapshots,
    private readonly reconcileMilliseconds = 10_000) {
    if (!Number.isFinite(reconcileMilliseconds) || reconcileMilliseconds < 1) throw new Error('Invalid reconciliation interval');
  }
  getSnapshot = () => this.#state;
  subscribe = (listener: () => void) => { this.#listeners.add(listener); return () => { this.#listeners.delete(listener); }; };
  #publish(update: Partial<State>) { this.#state = Object.freeze({ ...this.#state, ...update }); for (const listener of this.#listeners) listener(); }
  #store(value: unknown) {
    const snapshot = readStandings(value, this.tournament);
    if (!this.#state.snapshot || BigInt(snapshot.snapshotVersion) >= BigInt(this.#state.snapshot.snapshotVersion))
      this.#publish({ snapshot, error: undefined });
  }
  start() {
    this.stop();
    if (!this.tournament) return;
    this.#active = true;
    this.#failedConnections = 0;
    this.#publish({ loading: false, connection: 'connecting' });
    this.#connect(this.#generation);
    void this.refresh();
  }
  #connect(generation: number) {
    this.#unsubscribe?.();
    const disconnected = () => {
      if (!this.#active || generation !== this.#generation || this.#retry !== undefined) return;
      this.#publish({ connection: 'degraded' });
      const delay = this.reconcileMilliseconds * 2 ** Math.min(this.#failedConnections++, 3);
      this.#retry = setTimeout(() => {
        this.#retry = undefined;
        this.#connect(generation);
        if (this.#state.connection !== 'live') void this.refresh();
      }, delay);
    };
    try {
      this.#unsubscribe = this.events(this.tournament, value => {
        if (!this.#active || generation !== this.#generation) return;
        this.#store(value);
        clearTimeout(this.#retry); this.#retry = undefined;
        this.#failedConnections = 0;
        this.#publish({ connection: 'live', error: undefined });
      }, disconnected);
    } catch { disconnected(); }
  }
  refresh = async () => {
    if (!this.#active || this.#cancel) return;
    const cancellation = new AbortController(); this.#cancel = cancellation;
    this.#publish({ loading: true });
    try {
      const value = await this.read(cancellation.signal, this.#state.snapshot?.snapshotVersion);
      if (cancellation.signal.aborted) return;
      if (value !== undefined || !this.#state.snapshot) this.#store(value);
      this.#publish({ error: undefined });
    } catch (error) {
      if (!cancellation.signal.aborted && this.#state.connection !== 'live') this.#publish({ error, connection: 'degraded' });
    } finally {
      if (this.#cancel === cancellation) { this.#cancel = undefined; this.#publish({ loading: false }); }
    }
  };
  stop() {
    this.#active = false; ++this.#generation; this.#cancel?.abort(); this.#cancel = undefined; this.#unsubscribe?.();
    clearTimeout(this.#retry); this.#retry = undefined; this.#unsubscribe = undefined;
  }
}
