import type { components } from '../../shared/api/generated/appeals';
import { ApiProtocolError } from '../../shared/api/http';
import type { Standings } from '../results/standings';
export type AppealView = components['schemas']['AppealView'];
export type AppealSummary = components['schemas']['AppealSummary'];
export const states: Record<AppealSummary['state'], string> = {
  Registered: 'В очереди на перепроверку', AcceptedAwaitingRecheck: 'Ожидает назначения', RecheckPending: 'На перепроверке',
  AwaitingSeniorReview: 'Ожидает старшего жюри', AwaitingResultApplication: 'Ожидает публикации результата', Closed: 'Завершена', NeedsResolution: 'Требует внимания',
};
export const exactVersion = (v: unknown): v is string => typeof v === 'string' && /^[1-9][0-9]*$/.test(v);
export function reviewLabel(v: AppealView): string { return v.reviewState === 'SeniorAssigned' ? 'Перепроверка назначена старшему жюри' : v.reviewState === 'SeniorQueued' ? 'Перепроверка в очереди старшего жюри' : states[v.state]; }
export function taskName(item: AppealSummary, standings: Standings | undefined) {
  return standings?.tasks.find(task => task.taskId === item.taskId)?.name ?? `Задача ${item.taskId}`;
}
export function appealName(item: AppealSummary, standings: Standings | undefined) {
  const name = standings?.teams.find(team => team.teamId === item.teamId)?.name;
  return `${name ? `Команда «${name}»` : `Команда ${item.teamId}`} · ${taskName(item, standings)}`;
}
const record = (v: unknown): v is Record<string, unknown> => v !== null && typeof v === 'object' && !Array.isArray(v);
const text = (v: unknown): v is string => typeof v === 'string' && v.length > 0;
const fields = ['appealId', 'tournamentId', 'teamId', 'taskId', 'state', 'submittedAt', 'version'];
function summary(v: unknown, tournament: string): v is AppealSummary & Record<string, unknown> {
  return record(v) && v.tournamentId === tournament && ['appealId', 'teamId', 'taskId'].every(k => text(v[k]))
    && typeof v.state === 'string' && Object.hasOwn(states, v.state) && exactVersion(v.version)
    && typeof v.submittedAt === 'string' && v.submittedAt.endsWith('Z') && Number.isFinite(Date.parse(v.submittedAt));
}
export function readView(v: unknown, tournament: string): AppealView {
  if (!summary(v, tournament) || !record(v) || Object.keys(v).some(k => ![...fields, 'resultId', 'resultVersion', 'baseCheckId', 'basePublicationId', 'finalPublicationId', 'reviewState', 'baseOutcome', 'recheckRequestId', 'checkId'].includes(k))
    || !['resultId', 'baseCheckId', 'basePublicationId'].every(k => text(v[k])) || !exactVersion(v.resultVersion)
    || !['Correct', 'Incorrect'].includes(String(v.baseOutcome)) || ['finalPublicationId', 'recheckRequestId', 'checkId'].some(k => k in v && !text(v[k]))) throw new ApiProtocolError();
  if ('reviewState' in v && (v.state !== 'AwaitingSeniorReview' || !['SeniorQueued', 'SeniorAssigned'].includes(String(v.reviewState)))) throw new ApiProtocolError();
  return v as unknown as AppealView;
}
export function readList(v: unknown, tournament: string, team?: string): AppealSummary[] {
  if (!record(v) || Object.keys(v).some(k => !['tournamentId', 'items'].includes(k)) || v.tournamentId !== tournament || !Array.isArray(v.items)
    || !v.items.every(i => summary(i, tournament) && Object.keys(i).every(k => fields.includes(k)) && (team === undefined || i.teamId === team))
    || new Set(v.items.map(i => i.appealId)).size !== v.items.length) throw new ApiProtocolError();
  return v.items as AppealSummary[];
}
export function readQueue(v: unknown, tournament: string) {
  if (!record(v)) throw new ApiProtocolError();
  const { nextCursor, ...list } = v;
  if (nextCursor !== undefined && (!text(nextCursor) || nextCursor.length > 256)) throw new ApiProtocolError();
  return { items: readList(list, tournament), nextCursor };
}
export function scheduleAppealRefresh(refresh: () => Promise<void> | undefined, initialRead?: Promise<void>): () => void {
  let stopped = false;
  let timer: ReturnType<typeof setTimeout> | undefined;
  function schedule() {
    if (stopped) return;
    timer = setTimeout(async () => {
      await refresh();
      schedule();
    }, 5000);
  }
  void Promise.resolve(initialRead).then(schedule);
  return () => { stopped = true; clearTimeout(timer); };
}
