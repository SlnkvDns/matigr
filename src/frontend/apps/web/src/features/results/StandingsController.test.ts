import { afterEach, expect, it, vi } from 'vitest';
import { StandingsController } from './StandingsController';
import type { SnapshotSubscription } from './StandingsController';
import { deferred } from '../../shared/auth/test-support';

afterEach(() => vi.useRealTimers());
const view = (version: string) => ({ tournamentId: 't', snapshotVersion: version, tasks: [], teams: [] });
const flush = async () => { await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); };
function events() {
  let snapshot!: (value: unknown) => void;
  let disconnect!: () => void;
  const close = vi.fn();
  const subscribe = vi.fn<SnapshotSubscription>((_t, next, down) => { snapshot = next; disconnect = down; return close; });
  return { subscribe, snapshot: (v: string) => snapshot(view(v)), disconnect: () => disconnect(), close };
}
it('takes an update during GET and ignores older responses and frames above 2^53', async () => {
  const source = events(); const first = deferred<unknown>();
  const read = vi.fn().mockReturnValueOnce(first.promise);
  const c = new StandingsController('t', read, source.subscribe); c.start();
  source.snapshot('9007199254740994'); first.resolve(view('9007199254740993')); await flush();
  source.snapshot('9007199254740993');
  expect(c.getSnapshot().snapshot?.snapshotVersion).toBe('9007199254740994');
  expect(read).toHaveBeenCalledTimes(1); c.stop();
});
it('polls and reconnects only while degraded, sending its current version with the next GET', async () => {
  vi.useFakeTimers(); const source = events();
  const read = vi.fn().mockResolvedValueOnce(view('1')).mockResolvedValueOnce(view('3')).mockResolvedValue(undefined);
  const c = new StandingsController('t', read, source.subscribe, 100);
  c.start(); await flush();
  source.snapshot('2');
  await vi.advanceTimersByTimeAsync(10_000);
  expect(read).toHaveBeenCalledTimes(1);
  source.disconnect();
  await vi.advanceTimersByTimeAsync(100);
  expect(read).toHaveBeenLastCalledWith(expect.any(AbortSignal), '2');
  expect(c.getSnapshot().snapshot?.snapshotVersion).toBe('3');
  expect(c.getSnapshot().connection).toBe('degraded');
  source.snapshot('4');
  await vi.advanceTimersByTimeAsync(10_000);
  expect(c.getSnapshot().connection).toBe('live');
  expect(source.subscribe).toHaveBeenCalledTimes(2);
  expect(read).toHaveBeenCalledTimes(2); c.stop();
});
it('a stopped generation ignores late response even when fetch ignores abort', async () => {
  const source = events(); const pending = deferred<unknown>();
  const read = vi.fn().mockReturnValueOnce(pending.promise).mockResolvedValue(view('2'));
  const c = new StandingsController('t', read, source.subscribe); c.start(); c.stop(); c.start(); await flush();
  pending.resolve(view('1')); await flush(); expect(c.getSnapshot().snapshot?.snapshotVersion).toBe('2'); expect(source.close).toHaveBeenCalled(); c.stop();
});
it('dependency errors preserve the latest snapshot and conditional GET with no body preserves it', async () => {
  const source = events();
  const read = vi.fn().mockResolvedValueOnce(view('1')).mockRejectedValueOnce(new Error('down')).mockResolvedValue(undefined);
  const c = new StandingsController('t', read, source.subscribe); c.start(); await flush(); await c.refresh();
  expect(c.getSnapshot().snapshot?.snapshotVersion).toBe('1'); expect(c.getSnapshot().connection).toBe('degraded');
  await c.refresh();
  expect(c.getSnapshot().snapshot?.snapshotVersion).toBe('1'); expect(c.getSnapshot().error).toBeUndefined(); c.stop();
});
