import type { components } from '../../shared/api/generated/appeals';
import { ApiProtocolError } from '../../shared/api/http';
export type AppealView = components['schemas']['AppealView'];
export type AppealSummary = components['schemas']['AppealSummary'];
export const states: Record<AppealSummary['state'], string> = {
  Registered: 'Ожидает ручного выбора жюри', AcceptedAwaitingRecheck: 'Ожидает подтверждения назначения', RecheckPending: 'Идёт перепроверка',
  AwaitingSeniorReview: 'Ожидает старшего жюри', AwaitingResultApplication: 'Ожидает применения результата', Closed: 'Завершена', NeedsResolution: 'Требуется разбор конфликта',
};
export const exactVersion = (v: unknown): v is string => typeof v === 'string' && /^[1-9][0-9]*$/.test(v);
const record = (v: unknown): v is Record<string, unknown> => v !== null && typeof v === 'object' && !Array.isArray(v);
const text = (v: unknown) => typeof v === 'string' && v.length > 0;
const fields = ['appealId', 'tournamentId', 'teamId', 'taskId', 'state', 'submittedAt', 'version'];
function summary(v: unknown, tournament: string): v is AppealSummary & Record<string, unknown> {
  return record(v) && v.tournamentId === tournament && ['appealId', 'teamId', 'taskId'].every(k => text(v[k]))
    && typeof v.state === 'string' && Object.hasOwn(states, v.state) && exactVersion(v.version)
    && typeof v.submittedAt === 'string' && v.submittedAt.endsWith('Z') && Number.isFinite(Date.parse(v.submittedAt));
}
export function readView(v: unknown, tournament: string): AppealView {
  if (!summary(v, tournament) || !record(v) || Object.keys(v).some(k => ![...fields, 'resultId', 'resultVersion', 'baseCheckId', 'basePublicationId', 'finalPublicationId', 'baseOutcome', 'recheckRequestId', 'checkId'].includes(k))
    || !['resultId', 'baseCheckId', 'basePublicationId'].every(k => text(v[k])) || !exactVersion(v.resultVersion)
    || !['Correct', 'Incorrect'].includes(String(v.baseOutcome)) || ['finalPublicationId', 'recheckRequestId', 'checkId'].some(k => k in v && !text(v[k]))) throw new ApiProtocolError();
  return v as unknown as AppealView;
}
export function readList(v: unknown, tournament: string, team?: string): AppealSummary[] {
  if (!record(v) || Object.keys(v).some(k => !['tournamentId', 'items'].includes(k)) || v.tournamentId !== tournament || !Array.isArray(v.items)
    || !v.items.every(i => summary(i, tournament) && Object.keys(i).every(k => fields.includes(k)) && (team === undefined || i.teamId === team))
    || new Set(v.items.map(i => i.appealId)).size !== v.items.length) throw new ApiProtocolError();
  return v.items as AppealSummary[];
}
