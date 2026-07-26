export function reportOperationalFailure(code: string, correlationId?: string): void {
  if (!import.meta.env.DEV) return
  const safeCode = /^[a-z0-9_.-]{1,80}$/i.test(code) ? code : 'unexpected_failure'
  const safeCorrelation = correlationId && /^[0-9a-f-]{36}$/i.test(correlationId) ? correlationId : undefined
  console.warn('[PurpleGlass]', safeCode, safeCorrelation ? { correlationId: safeCorrelation } : undefined)
}
