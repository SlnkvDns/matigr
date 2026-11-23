import { createContext, Fragment, useContext, useEffect, useState, useSyncExternalStore } from 'react';
import type { ReactNode } from 'react';
import type { IdentityProvider } from './identity';
import { SessionController } from './SessionController';

const SessionContext = createContext<SessionController | null>(null);

export function SessionProvider({ provider, children }: { provider?: IdentityProvider | undefined; children: ReactNode }) {
  const [session] = useState(() => new SessionController(provider));
  // No constructor side effects; cleanup also works with the StrictMode setup/cleanup cycle.
  useEffect(() => () => session.signOut(), [session]);
  return <SessionContext value={session}>{children}</SessionContext>;
}
export function useSession() {
  const session = useContext(SessionContext);
  if (!session) throw new Error('SessionProvider is required');
  const snapshot = useSyncExternalStore(session.subscribe, session.getSnapshot);
  return { session, snapshot };
}

export function PrivateSessionBoundary({ children }: { children: ReactNode }) {
  const { snapshot } = useSession();
  return <Fragment key={snapshot.revision}>{children}</Fragment>;
}
