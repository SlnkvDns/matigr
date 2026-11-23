const startupNetworkErrors = new Set([
  'ECONNREFUSED', 'ECONNRESET', 'EPIPE', 'UND_ERR_SOCKET', 'UND_ERR_CONNECT_TIMEOUT'
]);

export async function waitForHttpReady(url, description, timeout = 60000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const remaining = deadline - Date.now();
    if (remaining <= 0) break;
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(Math.min(2000, remaining)) });
      await response.body?.cancel();
      if (response.status === 200) return;
    } catch (error) {
      // Read-only readiness probes can race the listener during an expected restart.
      // Do not hide malformed URLs, assertions, or failures of business commands.
      if (error.name !== 'TimeoutError'
          && !(error instanceof TypeError && startupNetworkErrors.has(error.cause?.code))) throw error;
    }
    await new Promise(resolve => setTimeout(resolve, Math.max(0, Math.min(200, deadline - Date.now()))));
  }
  throw new Error(`Timed out: ${description}`);
}
