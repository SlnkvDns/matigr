import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import type { components } from '../../shared/api/generated/results';
import { ApiProtocolError } from '../../shared/api/http';
import { PrivateSessionBoundary, useSession } from '../../shared/auth/SessionContext';
import { ApiProblemNotice } from '../../shared/ui/ApiProblemNotice';

type Mine = components['schemas']['MyTournamentResults'];
export function readTeamResults(value: unknown, tournament: string): Mine {
  const exact = (v: unknown) => typeof v === 'string' && /^(0|[1-9][0-9]*)$/.test(v);
  const text = (v: unknown) => typeof v === 'string' && v.length > 0;
  const record = (v: unknown): v is Record<string, unknown> => v !== null && typeof v === 'object' && !Array.isArray(v);
  if (!record(value) || Object.keys(value).some(x => !['tournamentId', 'results'].includes(x))
    || value.tournamentId !== tournament || !Array.isArray(value.results)
    || !value.results.every(r => record(r) && Object.keys(r).every(x => ['taskId', 'resultId', 'resultVersion', 'publicationId', 'outcome', 'score', 'appealAvailabilityHint'].includes(x))
      && text(r.taskId) && text(r.resultId) && text(r.publicationId) && exact(r.resultVersion) && r.resultVersion !== '0'
      && exact(r.score) && ['Correct', 'Incorrect'].includes(String(r.outcome)) && typeof r.appealAvailabilityHint === 'string')
    || new Set(value.results.map(r => r.taskId)).size !== value.results.length) throw new ApiProtocolError();
  return value as unknown as Mine;
}

type TeamResultsProps = { tournament: string; snapshotVersion?: string | undefined; tasks?: components['schemas']['StandingsSnapshot']['tasks'] | undefined };
export function TeamResults(props: TeamResultsProps) {
  return <PrivateSessionBoundary><PrivateResults key={props.tournament} {...props} /></PrivateSessionBoundary>;
}
function PrivateResults({ tournament, snapshotVersion, tasks }: TeamResultsProps) {
  const { session, snapshot } = useSession();
  const revision = snapshot.revision;
  const allowed = snapshot.profile?.roles.includes('Team') && snapshot.profile.tournamentIds.includes(tournament);
  const [mine, setMine] = useState<Mine>();
  const [error, setError] = useState<unknown>();
  const [loading, setLoading] = useState(false);
  const reading = useRef<{ abort: AbortController; refreshQueued: boolean } | undefined>(undefined);
  const load = useCallback(async function load() {
    if (session.getSnapshot().revision !== revision) return;
    if (reading.current) {
      reading.current.refreshQueued = true;
      return;
    }
    const pending = { abort: new AbortController(), refreshQueued: false };
    reading.current = pending;
    const isCurrent = () => reading.current === pending && session.getSnapshot().revision === revision;
    setLoading(true);
    setError(undefined);
    try {
      const response = await session.workspace().client('/api/v1/results').get<unknown>(
        `/me/tournaments/${encodeURIComponent(tournament)}/results`, { signal: pending.abort.signal });
      if (isCurrent()) setMine(readTeamResults(response.data, tournament));
    } catch (error) {
      if (isCurrent()) setError(error);
    } finally {
      if (isCurrent()) {
        reading.current = undefined;
        if (pending.refreshQueued) void load();
        else setLoading(false);
      }
    }
  }, [session, revision, tournament]);
  useEffect(() => () => {
    const pending = reading.current;
    reading.current = undefined;
    pending?.abort.abort();
  }, []);
  useEffect(() => {
    if (allowed) void load();
  }, [allowed, snapshotVersion, load]);
  if (!allowed) return null;
  return <section aria-label="Мои результаты"><h2>Результаты вашей команды</h2>
    <button disabled={loading} onClick={() => void load()}>Обновить мои результаты</button>
    {loading && <p role="status">Загрузка результатов команды…</p>}
    {error !== undefined && <ApiProblemNotice error={error} />}
    {mine && mine.results.length === 0 && <p>Опубликованных результатов пока нет. Ожидайте завершения проверки.</p>}
    {mine && mine.results.length > 0 && <ul>{mine.results.map(r => <li key={r.taskId}>
      {tasks?.find(task => task.taskId === r.taskId)?.name ?? `Задача ${r.taskId}`}: {r.outcome === 'Correct' ? 'Правильно' : 'Неправильно'}, баллы: {r.score}.{' '}
      <Link to={`/appeals?${new URLSearchParams({ tournamentId: tournament, resultId: r.resultId, expectedResultVersion: r.resultVersion })}`}>Перейти к апелляции</Link>
    </li>)}</ul>}
    {mine && <p>В списке показаны опубликованные результаты. Остальные задачи ожидают проверки. Допустимость подачи проверяет сервис апелляций.</p>}
  </section>;
}
