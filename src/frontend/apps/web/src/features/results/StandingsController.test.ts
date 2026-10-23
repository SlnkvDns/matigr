import { afterEach, expect, it, vi } from 'vitest';
import { StandingsController } from './StandingsController';
import type { SnapshotSubscription } from './StandingsController';
import { deferred } from '../../shared/auth/test-support';

afterEach(() => vi.useRealTimers());
const view = (version: string) => ({ tournamentId: 't', snapshotVersion: version, tasks: [], teams: [] });
const flush = async () => { await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); };
function events() {
  let version!: (v: string) => void; let state!: (live: boolean) => void; const close = vi.fn();
  const subscribe: SnapshotSubscription = (_t, v, s) => { version = v; state = s; return close; };
  return { subscribe, version: (v: string) => version(v), state: (live: boolean) => state(live), close };
}
it('subscribes before GET; an event during GET forces reconciliation with exact versions above 2^53', async () => {
  const source = events(); const first = deferred<unknown>();
  const read = vi.fn().mockImplementationOnce(() => first.promise).mockResolvedValue(view('9007199254740994'));
  const c = new StandingsController('t', read, source.subscribe); c.start();
  source.version('9007199254740994'); first.resolve(view('9007199254740993')); await flush(); await flush();
  expect(c.getSnapshot().snapshot?.snapshotVersion).toBe('9007199254740994'); expect(read).toHaveBeenCalledTimes(2); c.stop();
});
it('old GET and old invalidations never roll back a newer snapshot', async () => {
  const source = events(); const read = vi.fn().mockResolvedValueOnce(view('12')).mockResolvedValue(view('11'));
  const c = new StandingsController('t', read, source.subscribe); c.start(); await flush();
  source.version('10'); await c.refresh(); expect(c.getSnapshot().snapshot?.snapshotVersion).toBe('12'); c.stop();
});
it('reconnect and periodic reconciliation recover missed invalidation and expose degraded state', async () => {
  vi.useFakeTimers(); const source = events(); let version = '1';
  const c = new StandingsController('t', async () => view(version), source.subscribe, 100);
  c.start(); await flush(); source.state(false); expect(c.getSnapshot().connection).toBe('degraded');
  version = '2'; await vi.advanceTimersByTimeAsync(100); expect(c.getSnapshot().snapshot?.snapshotVersion).toBe('2');
  version = '3'; source.state(true); await flush(); expect(c.getSnapshot().snapshot?.snapshotVersion).toBe('3'); c.stop();
});
it('a stopped generation ignores late response even when fetch ignores abort', async () => {
  const source = events(); const pending = deferred<unknown>(); const read = vi.fn().mockReturnValueOnce(pending.promise).mockResolvedValue(view('2'));
  const c = new StandingsController('t', read, source.subscribe); c.start(); c.stop(); c.start(); await flush();
  pending.resolve(view('1')); await flush(); expect(c.getSnapshot().snapshot?.snapshotVersion).toBe('2'); expect(source.close).toHaveBeenCalled(); c.stop();
});
it('dependency errors preserve the last snapshot and retry is explicit or periodic, without a hot loop', async () => {
  const source = events(); const read = vi.fn().mockResolvedValueOnce(view('1')).mockRejectedValue(new Error('down'));
  const c = new StandingsController('t', read, source.subscribe); c.start(); await flush(); await c.refresh();
  expect(c.getSnapshot().snapshot?.snapshotVersion).toBe('1'); expect(c.getSnapshot().connection).toBe('degraded'); expect(read).toHaveBeenCalledTimes(2); c.stop();
});
