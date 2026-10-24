import { useEffect, useRef, useState } from 'react';
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

export function TeamResults({ tournament, snapshotVersion }: { tournament: string; snapshotVersion?: string | undefined }) {
  return <PrivateSessionBoundary><PrivateResults tournament={tournament} snapshotVersion={snapshotVersion} /></PrivateSessionBoundary>;
}
function PrivateResults({ tournament, snapshotVersion }: { tournament: string; snapshotVersion?: string | undefined }) {
  const { session, snapshot } = useSession();
  const allowed = snapshot.profile?.roles.includes('Team') && snapshot.profile.tournamentIds.includes(tournament);
  const [mine, setMine] = useState<Mine>(); const [error, setError] = useState<unknown>();
  const [loading, setLoading] = useState(false); const sequence = useRef(0);
  const cancellation = useRef<AbortController | undefined>(undefined);
  async function load() {
    const generation = ++sequence.current; cancellation.current?.abort();
    const abort = new AbortController(); cancellation.current = abort;
    setLoading(true); setError(undefined);
    try {
      const r = await session.workspace().client('/api/v1/results').get<unknown>(`/me/tournaments/${encodeURIComponent(tournament)}/results`, { signal: abort.signal });
      const value = readTeamResults(r.data, tournament);
      if (generation === sequence.current && !abort.signal.aborted) setMine(value);
    } catch (e) { if (generation === sequence.current && !abort.signal.aborted) setError(e); }
    finally { if (generation === sequence.current && !abort.signal.aborted) setLoading(false); }
  }
  useEffect(() => {
    setMine(undefined);
    if (allowed) void load();
    return () => { ++sequence.current; cancellation.current?.abort(); };
  }, [allowed, tournament, snapshotVersion]);
  if (!allowed) return null;
  return <section aria-label="Мои результаты"><h2>Результаты вашей команды</h2>
    <button disabled={loading} onClick={() => void load()}>Обновить мои результаты</button>
    {loading && <p role="status">Загрузка результатов команды…</p>}
    {error !== undefined && <ApiProblemNotice error={error} />}
    {mine && mine.results.length === 0 && <p>Опубликованных результатов пока нет. Ожидайте завершения проверки.</p>}
    {mine && mine.results.length > 0 && <ul>{mine.results.map(r => <li key={r.taskId}>
      Задача {r.taskId}: {r.outcome === 'Correct' ? 'Правильно' : 'Неправильно'}, баллы: {r.score}.{' '}
      <Link to={`/appeals?${new URLSearchParams({ tournamentId: tournament, resultId: r.resultId, expectedResultVersion: r.resultVersion })}`}>Перейти к апелляции</Link>
    </li>)}</ul>}
    {mine && <p>В списке показаны опубликованные результаты. Остальные задачи ожидают проверки. Допустимость подачи проверяет сервис апелляций.</p>}
  </section>;
}
