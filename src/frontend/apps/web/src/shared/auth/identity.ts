import type { components } from '../api/generated/checking';
import { ApiProtocolError, createHttpClient } from '../api/http';

type WireProfile = components['schemas']['DevProfile'];
export type UserRole = WireProfile['roles'][number];
export type UserProfile = Readonly<Omit<WireProfile, 'roles' | 'tournamentIds'>> & {
  readonly roles: readonly UserRole[];
  readonly tournamentIds: readonly string[];
};
export interface SessionCredential {
  readonly accessToken: string;
  readonly expiresAt: number;
  readonly profile: UserProfile;
}
export interface IdentityProvider {
  readonly choices: readonly { readonly key: string; readonly label: string }[];
  select(key: string, signal: AbortSignal): Promise<SessionCredential>;
}

function record(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}
function nonempty(value: unknown): value is string { return typeof value === 'string' && value.trim().length > 0; }
function uniqueStrings(value: unknown): value is string[] {
  return Array.isArray(value) && value.every(nonempty) && new Set(value).size === value.length;
}
const roles: readonly string[] = ['Jury', 'Senior', 'Team', 'Organizer'];

// Copy only contract fields. The browser never extracts authority from JWT payloads.
export function readCredential(value: unknown): SessionCredential {
  if (!record(value) || !nonempty(value.accessToken) || value.tokenType !== 'Bearer'
    || typeof value.expiresAt !== 'string' || !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?Z$/.test(value.expiresAt)
    || !Number.isFinite(Date.parse(value.expiresAt)) || Date.parse(value.expiresAt) <= Date.now()
    || !record(value.profile)) throw new ApiProtocolError();
  const profile = value.profile;
  if (!nonempty(profile.subjectId) || typeof profile.displayName !== 'string'
    || !uniqueStrings(profile.roles) || profile.roles.length === 0 || profile.roles.some(role => !roles.includes(role))
    || !uniqueStrings(profile.tournamentIds)
    || (profile.roles.includes('Team') ? !nonempty(profile.teamId) : profile.teamId !== undefined)) throw new ApiProtocolError();
  return Object.freeze({
    accessToken: value.accessToken, expiresAt: Date.parse(value.expiresAt),
    profile: Object.freeze({ subjectId: profile.subjectId, displayName: profile.displayName,
      roles: Object.freeze([...profile.roles]) as readonly UserRole[], tournamentIds: Object.freeze([...profile.tournamentIds]),
      ...(profile.roles.includes('Team') ? { teamId: profile.teamId as string } : {}) }),
  });
}

export function createDevIdentityProvider(): IdentityProvider {
  // Public selector keys/labels only: no manifest import, IDs, roles or scope from fixtures.
  return {
    choices: [
      { key: 'jury-a', label: 'Жюри 1' }, { key: 'jury-b', label: 'Жюри 2' },
      { key: 'senior', label: 'Старший жюри' }, { key: 'organizer', label: 'Организатор' },
      { key: 'team-alpha', label: 'Команда Альфа' }, { key: 'team-beta', label: 'Команда Бета' },
      { key: 'team-gamma', label: 'Команда Гамма' },
    ],
    async select(key, signal) {
      const response = await createHttpClient('/dev/v1').post<unknown>('/session/select', { testSubjectId: key }, { signal });
      if (response.status !== 200) throw new ApiProtocolError();
      return readCredential(response.data);
    },
  };
}
