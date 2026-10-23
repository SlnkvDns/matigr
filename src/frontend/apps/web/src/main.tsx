import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router';
import { AppRoutes } from './app/AppRoutes';
import './app/styles.css';
import { SessionProvider } from './shared/auth/SessionContext';
import { createDevIdentityProvider } from './shared/auth/identity';

const root = document.getElementById('root');
if (!root) throw new Error('Application root is missing');
createRoot(root).render(
  <StrictMode>
    <SessionProvider provider={import.meta.env.DEV ? createDevIdentityProvider() : undefined}>
      <BrowserRouter>
        <AppRoutes />
      </BrowserRouter>
    </SessionProvider>
  </StrictMode>,
);
