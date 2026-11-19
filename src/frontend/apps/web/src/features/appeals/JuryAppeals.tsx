import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import type { components as checking } from '../../shared/api/generated/checking';
import { ApiError, ApiProtocolError } from '../../shared/api/http';
import { useSession } from '../../shared/auth/SessionContext';
import { ApiProblemNotice } from '../../shared/ui/ApiProblemNotice';
import { appealName, exactVersion, readList, readView, reviewLabel, states } from './appeals';
import type { AppealSummary, AppealView } from './appeals';
import type { Standings } from '../results/standings';

const submittedAt = new Intl.DateTimeFormat('ru-RU', { dateStyle: 'medium', timeStyle: 'short' });

export function JuryAppeals({ tournament, standings }: { tournament: string; standings?: Standings | undefined }) {
  const { session, snapshot } = useSession(); const [items, setItems] = useState<AppealSummary[]>();
  const [selection, setSelection] = useState<AppealSummary>();
  const [detail, setDetail] = useState<{ view: AppealView; own: boolean }>(); const [error, setError] = useState<unknown>();
  const [loading, setLoading] = useState(false); const [sending, setSending] = useState(false); const [retry, setRetry] = useState(false);
  const selectionHeading = useRef<HTMLHeadingElement>(null);
  const accepted = useRef<{ appealId: string; version: string } | undefined>(undefined);
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
      if (accepted.current?.appealId === id && BigInt(view.version) < BigInt(accepted.current.version)) return;
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
    setItems(undefined); setSelection(undefined); setDetail(undefined); accepted.current = undefined;
    pending.current = undefined; inFlight.current = false; setSending(false); setRetry(false);
    if (tournament) void loadQueue();
    return () => { ++queueSequence.current; ++detailSequence.current; queueAbort.current?.abort(); detailAbort.current?.abort(); commandAbort.current?.abort(); };
  }, [tournament]);
  useEffect(() => {
    if (!selection) return;
    const refresh = () => { if (!inFlight.current && !pending.current) void loadDetail(selection.appealId); };
    window.addEventListener('focus', refresh); window.addEventListener('online', refresh);
    const timer = window.setInterval(refresh, 5000);
    return () => { window.clearInterval(timer); window.removeEventListener('focus', refresh); window.removeEventListener('online', refresh); };
  }, [selection?.appealId, tournament]);
  useEffect(() => { selectionHeading.current?.focus(); }, [selection?.appealId]);
  async function claim(item: AppealSummary) {
    if (inFlight.current) return; inFlight.current = true; setSending(true); setError(undefined);
    const operation = pending.current ?? { appealId: item.appealId, version: item.version, key: crypto.randomUUID() };
    pending.current = operation;
    ++detailSequence.current; detailAbort.current?.abort();
    setSelection(item); setDetail(undefined);
    const abort = new AbortController(); commandAbort.current = abort;
    try {
      const response = await session.workspace().client('/api/v1/appeals').command<{ appealId: string; state: string; version: string }>(`/${encodeURIComponent(operation.appealId)}/claim`, undefined,
        { idempotencyKey: operation.key, ifMatch: `"${operation.version}"`, signal: abort.signal });
      if (abort.signal.aborted) return;
      if (response.status !== 202 || response.data?.appealId !== operation.appealId || response.data.state !== 'AcceptedAwaitingRecheck' || !exactVersion(response.data.version)) throw new ApiProtocolError();
      accepted.current = { appealId: operation.appealId, version: response.data.version };
      setSelection({ ...item, state: 'AcceptedAwaitingRecheck', version: response.data.version });
      pending.current = undefined; setRetry(false);
      await Promise.all([loadDetail(operation.appealId), loadQueue(true)]);
    } catch (e) {
      if (abort.signal.aborted) return; setError(e); setRetry(true);
      if (e instanceof ApiError && [409, 412].includes(e.status)) { pending.current = undefined; setRetry(false); await Promise.all([loadDetail(operation.appealId), loadQueue(true)]); }
    } finally { if (!abort.signal.aborted) { inFlight.current = false; setSending(false); } }
  }
  const selected = detail?.view ?? selection;
  const awaiting = selected?.state === 'AcceptedAwaitingRecheck';
  const busy = detail?.view.state === 'Registered' && accepted.current?.appealId === detail.view.appealId;
  const blocked = sending || retry || awaiting || detail?.own;
  const queue = items?.filter(item => item.appealId !== selected?.appealId || (!busy && !sending && !retry && selected?.state === 'Registered'));
  const selectionMessage = sending ? 'Запрашиваем назначение…'
    : retry ? 'Пока неизвестно, принято ли назначение.'
    : detail?.own ? 'Перепроверка назначена вам.'
    : busy ? 'Апелляция вернулась в очередь. Если у вас есть текущая проверка, завершите её перед повторным выбором.'
    : detail ? reviewLabel(detail.view)
    : selected && states[selected.state];
  return <div className="appeals-workspace">
    {error !== undefined && <ApiProblemNotice error={error} />}
    {selected && <section aria-label="Выбранная апелляция" className="appeals-panel appeals-selection">
      <div className="appeals-panel-heading">
        <div><h2 ref={selectionHeading} tabIndex={-1}>Выбранная апелляция</h2><p className="appeals-item-name">{appealName(selected, standings)}</p></div>
        <button className="appeals-button appeals-secondary" disabled={sending || retry} onClick={() => void loadDetail(selected.appealId)}>Обновить статус</button>
      </div>
      <div role="status" className="appeals-feedback">
        {selectionMessage}
        {awaiting && !sending && !retry && <p>Запрос принят. Кнопка перехода появится, когда назначение подтвердится.</p>}
        {selected.state === 'AwaitingResultApplication' && <p>Вердикт сохранён. Итог появится в таблице после публикации.</p>}
        {selected.state === 'Closed' && <p>Итог перепроверки опубликован.</p>}
        {selected.state === 'NeedsResolution' && <p>Итог не удалось применить. Апелляция требует разбора.</p>}
      </div>
      <div className="appeals-actions">
        {retry && <button className="appeals-button" disabled={sending} onClick={() => void claim(selected)}>Повторить запрос</button>}
        {detail?.own && <Link className="appeals-button" to="/checking">Открыть перепроверку</Link>}
        {busy && <>
          <Link className="appeals-button appeals-secondary" to="/checking">К текущей проверке</Link>
          <button className="appeals-button" disabled={blocked} onClick={() => void claim(selected)}>Взять снова</button>
        </>}
      </div>
    </section>}
    <section aria-label="Очередь апелляций" className="appeals-panel">
      <div className="appeals-panel-heading">
        <div><h2>Очередь апелляций</h2><p>Возьмите одну апелляцию, чтобы лично перепроверить решение.</p></div>
        <button className="appeals-button appeals-secondary" disabled={loading || sending} onClick={() => void loadQueue()}>Обновить очередь</button>
      </div>
      {loading && <p role="status" className="appeals-empty">Загрузка очереди…</p>}
      {!loading && queue?.length === 0 && <p className="appeals-empty">{selected ? 'Нет других апелляций, ожидающих перепроверки.' : 'Очередь пока пуста.'}</p>}
      <ul className="appeals-list">{queue?.map(item => <li key={item.appealId}>
        <div><strong className="appeals-item-name">{appealName(item, standings)}</strong>
          <time dateTime={item.submittedAt}>Подана {submittedAt.format(new Date(item.submittedAt))}</time></div>
        <button className="appeals-button" disabled={blocked} onClick={() => void claim(item)}>Взять на перепроверку</button>
      </li>)}</ul>
    </section>
  </div>;
}
