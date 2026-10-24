import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import type { components as checking } from '../../shared/api/generated/checking';
import { ApiError, ApiProtocolError } from '../../shared/api/http';
import { useSession } from '../../shared/auth/SessionContext';
import { ApiProblemNotice } from '../../shared/ui/ApiProblemNotice';
import { exactVersion, readList, readView, states } from './appeals';
import type { AppealSummary, AppealView } from './appeals';

export function JuryAppeals({ tournament }: { tournament: string }) {
  const { session, snapshot } = useSession(); const [items, setItems] = useState<AppealSummary[]>([]);
  const [detail, setDetail] = useState<{ view: AppealView; own: boolean }>(); const [error, setError] = useState<unknown>();
  const [loading, setLoading] = useState(false); const [sending, setSending] = useState(false); const [retry, setRetry] = useState(false);
  const pending = useRef<{ appealId: string; version: string; key: string } | undefined>(undefined); const inFlight = useRef(false);
  const queueSequence = useRef(0); const detailSequence = useRef(0);
  const queueAbort = useRef<AbortController | undefined>(undefined); const detailAbort = useRef<AbortController | undefined>(undefined); const commandAbort = useRef<AbortController | undefined>(undefined);
  async function loadQueue(preserve = false) {
    const generation = ++queueSequence.current; queueAbort.current?.abort(); const abort = new AbortController(); queueAbort.current = abort;
    setLoading(true); if (!preserve) setError(undefined);
    try {
      const response = await session.workspace().client('/api/v1/appeals').get<unknown>(`/tournaments/${encodeURIComponent(tournament)}?state=Registered`, { signal: abort.signal });
      const rows = readList(response.data, tournament);
      if (generation === queueSequence.current && !abort.signal.aborted) setItems(rows);
    } catch (e) { if (generation === queueSequence.current && !abort.signal.aborted) setError(e); }
    finally { if (generation === queueSequence.current && !abort.signal.aborted) setLoading(false); }
  }
  async function loadDetail(id: string) {
    const generation = ++detailSequence.current; detailAbort.current?.abort(); const abort = new AbortController(); detailAbort.current = abort;
    try {
      const response = await session.workspace().client('/api/v1/appeals').get<unknown>(`/${encodeURIComponent(id)}`, { signal: abort.signal });
      const view = readView(response.data, tournament); if (view.appealId !== id) throw new ApiProtocolError();
      let own = false;
      if (view.checkId && (view.state === 'RecheckPending' || view.state === 'AwaitingSeniorReview')) {
        const current = await session.workspace().client('/api/v1/checking').get<checking['schemas']['MyAssignment']>('/me/assignment', { signal: abort.signal });
        own = current.data?.check.checkId === view.checkId && current.data?.reviewerId === snapshot.profile?.subjectId;
      }
      if (generation !== detailSequence.current || abort.signal.aborted) return;
      setDetail(old => old?.view.appealId === id && BigInt(old.view.version) > BigInt(view.version) ? old : { view, own });
    } catch (e) { if (generation === detailSequence.current && !abort.signal.aborted) setError(e); }
  }
  useEffect(() => {
    setItems([]); setDetail(undefined); pending.current = undefined; inFlight.current = false; setSending(false); setRetry(false);
    if (tournament) void loadQueue();
    return () => { ++queueSequence.current; ++detailSequence.current; queueAbort.current?.abort(); detailAbort.current?.abort(); commandAbort.current?.abort(); };
  }, [tournament]);
  useEffect(() => {
    if (!detail) return;
    const refresh = () => { if (!inFlight.current) void loadDetail(detail.view.appealId); };
    window.addEventListener('focus', refresh); window.addEventListener('online', refresh);
    const timer = window.setInterval(refresh, 5000);
    return () => { window.clearInterval(timer); window.removeEventListener('focus', refresh); window.removeEventListener('online', refresh); };
  }, [detail?.view.appealId, tournament]);
  async function claim(item: { appealId: string; version: string }) {
    if (inFlight.current) return; inFlight.current = true; setSending(true); setError(undefined);
    const operation = pending.current ?? { ...item, key: crypto.randomUUID() }; pending.current = operation;
    const abort = new AbortController(); commandAbort.current = abort;
    try {
      const response = await session.workspace().client('/api/v1/appeals').command<{ appealId: string; state: string; version: string }>(`/${encodeURIComponent(operation.appealId)}/claim`, undefined,
        { idempotencyKey: operation.key, ifMatch: `"${operation.version}"`, signal: abort.signal });
      if (abort.signal.aborted) return;
      if (response.status !== 202 || response.data?.appealId !== operation.appealId || response.data.state !== 'AcceptedAwaitingRecheck' || !exactVersion(response.data.version)) throw new ApiProtocolError();
      pending.current = undefined; setRetry(false);
      await Promise.all([loadDetail(operation.appealId), loadQueue(true)]);
    } catch (e) {
      if (abort.signal.aborted) return; setError(e); setRetry(true);
      if (e instanceof ApiError && [409, 412].includes(e.status)) { pending.current = undefined; setRetry(false); await Promise.all([loadDetail(operation.appealId), loadQueue(true)]); }
    } finally { if (!abort.signal.aborted) { inFlight.current = false; setSending(false); } }
  }
  return <>
    <button disabled={loading || sending} onClick={() => void loadQueue()}>Обновить очередь апелляций</button>
    {loading && <p role="status">Загрузка очереди…</p>}{error !== undefined && <ApiProblemNotice error={error} />}
    {retry && pending.current && <button disabled={sending} onClick={() => void claim(pending.current!)}>Повторить выбор</button>}
    <section aria-label="Очередь апелляций"><h2>Ожидают ручного выбора</h2>
      {!loading && items.length === 0 && <p>Очередь пуста.</p>}
      <ul>{items.map(item => <li key={item.appealId}>Команда {item.teamId}, задача {item.taskId}.{' '}
        <button disabled={sending || retry} onClick={() => void claim(item)}>Выбрать апелляцию</button></li>)}</ul>
    </section>
    {detail && <section aria-label="Выбранная апелляция"><h2>Выбранная апелляция</h2><p>{states[detail.view.state]}</p>
      {detail.view.state === 'AwaitingResultApplication' && <p>Вердикт получен. Ожидаем подтверждения публикации Results.</p>}
      {detail.view.state === 'Closed' && <p>Результат опубликован. Итоговая публикация: {detail.view.finalPublicationId}</p>}
      {detail.view.state === 'NeedsResolution' && <p>Результат не применён. Требуется разбор конфликта без повторной подачи.</p>}
      <button disabled={sending} onClick={() => void loadDetail(detail.view.appealId)}>Обновить выбранную апелляцию</button>
      {detail.view.state === 'AcceptedAwaitingRecheck' && <p>Выбор сохранён. Checking ещё не подтвердил назначение.</p>}
      {detail.view.state === 'Registered' && <><p>Апелляция снова ожидает ручного выбора. Повтор выполняется только вашим действием.</p>
        <button disabled={sending || retry} onClick={() => void claim(detail.view)}>Выбрать снова</button></>}
      {detail.own && <Link to="/checking">Открыть назначенную перепроверку</Link>}
    </section>}
  </>;
}
