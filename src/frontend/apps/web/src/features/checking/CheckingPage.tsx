import { useEffect, useRef, useState } from 'react';
import type { components } from '../../shared/api/generated/checking';
import { ApiError } from '../../shared/api/http';
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
  const [reading, setReading] = useState(false);
  const [commanding, setCommanding] = useState(false);
  const [materialLoading, setMaterialLoading] = useState(false);
  const [loaded, setLoaded] = useState(false);
  const busy = reading || commanding || materialLoading;
  const [pending, setPending] = useState<Pending>();
  const [notice, setNotice] = useState('');
  const active = useRef(true);
  const sequence = useRef(0);
  const inFlight = useRef(false);
  const abort = useRef<AbortController | undefined>(undefined);
  const materialAbort = useRef<AbortController | undefined>(undefined);
  const channel = useRef<BroadcastChannel | undefined>(undefined);
  useEffect(() => { if (!tournament && profile?.tournamentIds[0]) setTournament(profile.tournamentIds[0]); }, [profile, tournament]);

  async function load(cursor?: string) {
    const revision = ++sequence.current;
    abort.current?.abort(); const cancellation = new AbortController(); abort.current = cancellation;
    materialAbort.current?.abort(); setMaterialLoading(false);
    setReading(true); setLoaded(false); setError(undefined); setMaterial(undefined);
    try {
      const client = session.workspace().client('/api/v1/checking');
      const assignment = await client.get<Assignment>('/me/assignment', { signal: cancellation.signal });
      const available = await client.get<Queue>(`/queues?${new URLSearchParams({ tournamentId: tournament, queue, ...(cursor ? { cursor } : {}) })}`, { signal: cancellation.signal });
      if (!active.current || revision !== sequence.current) return;
      setCurrent(assignment.data); setEtag(assignment.etag ?? undefined); setPage(available.data); setLoaded(true);
    } catch (e) { if (active.current && !cancellation.signal.aborted && revision === sequence.current) setError(e); }
    finally { if (active.current && revision === sequence.current) setReading(false); }
  }
  useEffect(() => {
    active.current = true;
    if (permitted && tournament) void load();
    if (permitted && tournament) {
      const refresh = () => { void load(); };
      const visible = () => { if (document.visibilityState === 'visible') refresh(); };
      window.addEventListener('focus', refresh); window.addEventListener('online', refresh); window.addEventListener('pageshow', refresh);
      document.addEventListener('visibilitychange', visible);
      if (typeof BroadcastChannel !== 'undefined') {
        const updates = new BroadcastChannel('matigr-checking-changed'); channel.current = updates;
        updates.onmessage = event => { if (event.data?.type === 'changed') refresh(); };
      }
      return () => {
        active.current = false; ++sequence.current; abort.current?.abort(); materialAbort.current?.abort();
        window.removeEventListener('focus', refresh); window.removeEventListener('online', refresh); window.removeEventListener('pageshow', refresh);
        document.removeEventListener('visibilitychange', visible); channel.current?.close(); channel.current = undefined;
      };
    }
    return () => { active.current = false; ++sequence.current; abort.current?.abort(); materialAbort.current?.abort(); };
  }, [tournament, queue, permitted]);

  async function command(operation: Pending) {
    if (inFlight.current) return;
    inFlight.current = true; setCommanding(true); setError(undefined); setPending(operation);
    try {
      await session.workspace().client('/api/v1/checking').command(operation.path, operation.body,
        { idempotencyKey: operation.key, ...(operation.etag ? { ifMatch: operation.etag } : {}) });
      if (!active.current) return;
      setPending(undefined); setNotice(operation.path.endsWith('/refuse') ? 'Проверка передана в очередь старшего.'
        : operation.path.endsWith('/complete') ? 'Вердикт сохранён. Результаты обновляются автоматически.' : 'Запрос назначения выполнен.');
      channel.current?.postMessage({ type: 'changed' });
      await load();
    } catch (e) {
      if (!active.current) return;
      if (e instanceof ApiError && e.status < 500) setPending(undefined);
      if (e instanceof ApiError && (e.status === 409 || e.status === 412)) await load();
      if (active.current) setError(e);
    } finally { inFlight.current = false; if (active.current) setCommanding(false); }
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
    const revision = sequence.current;
    materialAbort.current?.abort(); const cancellation = new AbortController(); materialAbort.current = cancellation;
    setMaterialLoading(true); setError(undefined);
    try {
      const response = await session.workspace().client('/api/v1/checking').text(`/checks/${encodeURIComponent(checkId)}/material`, { signal: cancellation.signal });
      if (active.current && revision === sequence.current && !cancellation.signal.aborted) setMaterial(response.data);
    } catch (e) { if (active.current && revision === sequence.current && !cancellation.signal.aborted) setError(e); }
    finally { if (active.current && revision === sequence.current) setMaterialLoading(false); }
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
          <button disabled={busy || !!pending || !loaded} onClick={() => void openMaterial()}>Открыть материал</button>
          {material !== undefined && <pre aria-label="Материал решения">{material}</pre>}
          <div className="checking-actions">
            <button disabled={busy || !!pending || !loaded} onClick={() => verdict('Correct')}>Правильно</button>
            <button disabled={busy || !!pending || !loaded} onClick={() => verdict('Incorrect')}>Неправильно</button>
            {current.check.state === 'Assigned' && <button disabled={busy || !!pending || !loaded} onClick={() => verdict()}>Передать старшему</button>}
          </div>
        </> : <p>{loaded ? 'У вас нет незавершённой проверки.' : 'Назначение уточняется.'}</p>}
      </section>
      <section aria-label="Очередь проверок"><h2>Доступные проверки</h2>
        <p>Очередь не резервирует работу. Назначение подтверждает сервер.</p>
        <ul>{page?.items.map(item => <li key={item.checkId}>Задача {item.taskId} · команда {item.teamId}</li>)}</ul>
        {page?.items.length === 0 && <p>Очередь пуста.</p>}
        {page?.nextCursor && <button disabled={busy || !!pending} onClick={() => void load(page.nextCursor!)}>Следующая страница</button>}
        <button disabled={busy || !!current || !!pending || !loaded} onClick={() => void command({ path: '/me/assignments/next',
          body: { tournamentId: tournament, queue }, key: crypto.randomUUID() })}>Взять следующую</button>
      </section>
    </div>}
  </Page>;
}
