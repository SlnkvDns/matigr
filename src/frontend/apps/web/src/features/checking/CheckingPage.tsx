import { useEffect, useRef, useState } from 'react';
import type { components } from '../../shared/api/generated/checking';
import { ApiError, ApiNetworkError } from '../../shared/api/http';
import { useSession } from '../../shared/auth/SessionContext';
import { ApiProblemNotice } from '../../shared/ui/ApiProblemNotice';
import { Page } from '../../shared/ui/Page';
import './checking.css';

type Assignment = components['schemas']['MyAssignment'];
type Queue = components['schemas']['QueuePage'];
type Pending = { path: string; body: unknown; key: string; etag?: string };

export function CheckingPage() {
  const { snapshot, session } = useSession();
  const profile = snapshot.profile;
  const permitted = profile?.roles.some(r => r === 'Jury' || r === 'Senior');
  const [tournament, setTournament] = useState(profile?.tournamentIds[0] ?? '');
  const [queue, setQueue] = useState<'general' | 'senior'>('general');
  const [page, setPage] = useState<Queue>();
  const [current, setCurrent] = useState<Assignment>();
  const [etag, setEtag] = useState<string>();
  const [material, setMaterial] = useState<string>();
  const [error, setError] = useState<unknown>();
  const [busy, setBusy] = useState(false);
  const [pending, setPending] = useState<Pending>();
  const [notice, setNotice] = useState('');
  const active = useRef(true);
  const sequence = useRef(0);
  const inFlight = useRef(false);
  const abort = useRef<AbortController | undefined>(undefined);
  useEffect(() => { if (!tournament && profile?.tournamentIds[0]) setTournament(profile.tournamentIds[0]); }, [profile, tournament]);

  async function load(cursor?: string) {
    const revision = ++sequence.current;
    abort.current?.abort(); const cancellation = new AbortController(); abort.current = cancellation;
    setBusy(true); setError(undefined); setMaterial(undefined);
    try {
      const client = session.workspace().client('/api/v1/checking');
      const assignment = await client.get<Assignment>('/me/assignment', { signal: cancellation.signal });
      const available = await client.get<Queue>(`/queues?${new URLSearchParams({ tournamentId: tournament, queue, ...(cursor ? { cursor } : {}) })}`, { signal: cancellation.signal });
      if (!active.current || revision !== sequence.current) return;
      setCurrent(assignment.data); setEtag(assignment.etag ?? undefined); setPage(available.data);
    } catch (e) { if (active.current && !cancellation.signal.aborted && revision === sequence.current) setError(e); }
    finally { if (active.current && revision === sequence.current) setBusy(false); }
  }
  useEffect(() => {
    active.current = true;
    if (permitted && tournament) void load();
    return () => { active.current = false; ++sequence.current; abort.current?.abort(); };
  }, [tournament, queue, permitted]);

  async function command(operation: Pending) {
    if (inFlight.current) return;
    inFlight.current = true; setBusy(true); setError(undefined); setPending(operation);
    try {
      await session.workspace().client('/api/v1/checking').command(operation.path, operation.body,
        { idempotencyKey: operation.key, ...(operation.etag ? { ifMatch: operation.etag } : {}) });
      if (!active.current) return;
      setPending(undefined); setNotice(operation.path.endsWith('/refuse') ? 'Проверка передана в очередь старшего. Вы свободны.'
        : operation.path.endsWith('/complete') ? 'Вердикт сохранён. Результат ожидает обработки.' : 'Запрос назначения выполнен.');
      await load();
    } catch (e) {
      if (!active.current) return;
      setError(e);
      if (!(e instanceof ApiNetworkError) && !(e instanceof ApiError && e.status >= 500)) setPending(undefined);
    } finally { inFlight.current = false; if (active.current) setBusy(false); }
  }
  function verdict(outcome?: 'Correct' | 'Incorrect') {
    if (!current || !etag) return;
    void command({ path: `/checks/${encodeURIComponent(current.check.checkId)}/${outcome ? 'complete' : 'refuse'}`,
      body: { assignmentId: current.assignmentId, capability: current.capability, ...(outcome ? { outcome } : {}) },
      key: crypto.randomUUID(), etag });
  }
  async function openMaterial() {
    if (!current) return;
    const checkId = current.check.checkId;
    setBusy(true); setError(undefined);
    try {
      const response = await session.workspace().client('/api/v1/checking').text(`/checks/${encodeURIComponent(checkId)}/material`);
      if (active.current) setMaterial(response.data);
    } catch (e) { if (active.current) setError(e); }
    finally { if (active.current) setBusy(false); }
  }
  return <Page title="Проверка решений" description="Рабочее место жюри для проверки решений команд.">
    {!permitted ? <p>Для проверки войдите как член жюри.</p> : <div className="checking-work">
      <div className="checking-controls">
        <label>Турнир <select value={tournament} disabled={busy || !!pending} onChange={e => setTournament(e.target.value)}>
          {profile!.tournamentIds.map(id => <option key={id} value={id}>{id}</option>)}
        </select></label>
        <label>Очередь <select value={queue} disabled={busy || !!pending} onChange={e => setQueue(e.target.value as 'general' | 'senior')}>
          <option value="general">Общая</option>{profile!.roles.includes('Senior') && <option value="senior">Старшего жюри</option>}
        </select></label>
        <button disabled={busy} onClick={() => void load()}>Обновить</button>
      </div>
      {error !== undefined && <ApiProblemNotice error={error} />}
      {pending && !busy && <button onClick={() => void command(pending)}>Повторить прежний запрос</button>}
      {notice && <p role="status">{notice}</p>}
      {busy && <p role="status">Загрузка…</p>}
      <section aria-label="Текущая проверка"><h2>Ваша проверка</h2>
        {current ? <>
          <p>Задача: {current.check.taskId}</p><p>Команда: {current.check.teamId}</p>
          <p>{current.check.state === 'SeniorAssigned' ? 'Окончательный вердикт старшего' : 'Назначена вам'}</p>
          <button disabled={busy || !!pending} onClick={() => void openMaterial()}>Открыть материал</button>
          {material !== undefined && <pre aria-label="Материал решения">{material}</pre>}
          <div className="checking-actions">
            <button disabled={busy || !!pending} onClick={() => verdict('Correct')}>Правильно</button>
            <button disabled={busy || !!pending} onClick={() => verdict('Incorrect')}>Неправильно</button>
            {current.check.state === 'Assigned' && <button disabled={busy || !!pending} onClick={() => verdict()}>Передать старшему</button>}
          </div>
        </> : <p>У вас нет незавершённой проверки.</p>}
      </section>
      <section aria-label="Очередь проверок"><h2>Доступные проверки</h2>
        <p>Очередь не резервирует работу. Назначение подтверждает сервер.</p>
        <ul>{page?.items.map(item => <li key={item.checkId}>Задача {item.taskId} · команда {item.teamId}</li>)}</ul>
        {page?.items.length === 0 && <p>Очередь пуста.</p>}
        {page?.nextCursor && <button disabled={busy || !!pending} onClick={() => void load(page.nextCursor!)}>Следующая страница</button>}
        <button disabled={busy || !!current || !!pending} onClick={() => void command({ path: '/me/assignments/next',
          body: { tournamentId: tournament, queue }, key: crypto.randomUUID() })}>Взять следующую</button>
      </section>
    </div>}
  </Page>;
}
