import type { components } from '../../shared/api/generated/results';
import { ApiProtocolError } from '../../shared/api/http';
export type Standings = components['schemas']['StandingsSnapshot'];
const exact = (v: unknown): v is string => typeof v === 'string' && /^(0|[1-9][0-9]*)$/.test(v);
const record = (v: unknown): v is Record<string, unknown> => v !== null && typeof v === 'object' && !Array.isArray(v);
const text = (v: unknown): v is string => typeof v === 'string' && v.length > 0;
const fields = (v: Record<string, unknown>, names: string[]) => Object.keys(v).every(x => names.includes(x));
export function readStandings(value: unknown, tournament: string): Standings {
  if (!record(value) || !fields(value, ['tournamentId', 'snapshotVersion', 'tasks', 'teams'])
    || value.tournamentId !== tournament || !exact(value.snapshotVersion) || !Array.isArray(value.tasks) || !Array.isArray(value.teams)
    || !value.tasks.every(task => record(task) && fields(task, ['taskId', 'name']) && text(task.taskId) && text(task.name))
    || new Set(value.tasks.map(task => task.taskId)).size !== value.tasks.length
    || !value.teams.every(team => record(team) && fields(team, ['teamId', 'name', 'cells', 'total', 'rank'])
      && text(team.teamId) && text(team.name) && exact(team.total) && Number.isSafeInteger(team.rank) && Number(team.rank) > 0
      && Array.isArray(team.cells) && team.cells.length === (value.tasks as unknown[]).length
      && team.cells.every(cell => record(cell) && fields(cell, ['status', 'score'])
        && (cell.status === 'Pending' ? !('score' in cell) : cell.status === 'Published' && exact(cell.score))))
    || new Set(value.teams.map(team => team.teamId)).size !== value.teams.length) throw new ApiProtocolError();
  // Values/places come directly from Results. Never calculate authoritative totals, scores or ranks here.
  return value as unknown as Standings;
}
