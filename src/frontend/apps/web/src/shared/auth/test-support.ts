import type { SessionCredential } from './identity';

// Imported only by tests; no tournament fixture or private material in the bundle.
export function wireSession(name = 'jury-a') {
  return { accessToken: `memory-secret-${name}`, tokenType: 'Bearer', expiresAt: new Date(Date.now() + 3600000).toISOString(),
    profile: { subjectId: `subject-${name}`, displayName: name, roles: ['Jury'], tournamentIds: ['tournament-1'] } };
}
export function credential(name = 'jury-a', expiresAt = Date.now() + 3600000): SessionCredential {
  return { accessToken: `memory-secret-${name}`, expiresAt,
    profile: { subjectId: `subject-${name}`, displayName: name, roles: ['Jury'], tournamentIds: ['tournament-1'] } };
}
export function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
export function jsonResponse(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
}
