import { FormEvent, useState } from 'react'
import type { SupportedCallLanguage, TelephonyStatus } from '../services/prototypeApi'

const e164 = /^\+[1-9][0-9]{7,14}$/

export function isValidDestination(value: string): boolean { return e164.test(value.trim()) }

export function selectedLanguageCode(value: string, supportedLanguages: SupportedCallLanguage[]): string | undefined {
  return supportedLanguages.some((language) => language.code === value) ? value : undefined
}

export interface TelephonyControlsProps {
  status?: TelephonyStatus
  canInitiate: boolean
  loading: boolean
  error: boolean
  supportedLanguages?: SupportedCallLanguage[]
  locationDefaultLanguageCode?: string
  onStart(destination: string, languageCode?: string): Promise<void>
}

export function TelephonyControls({ status, canInitiate, loading, error, supportedLanguages = [],
  locationDefaultLanguageCode, onStart }: TelephonyControlsProps) {
  const [destination, setDestination] = useState('')
  const [languageCode, setLanguageCode] = useState('')
  const valid = isValidDestination(destination)
  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!valid || !canInitiate) return
    await onStart(destination.trim(), selectedLanguageCode(languageCode, supportedLanguages))
  }

  return <section className="panel telephony-control" aria-label="Telephony controls">
    <div>
      <p className="eyebrow">REAL CALL TRANSPORT</p>
      <h3>Start test call</h3>
      <p className="subtle">Provider: {status?.provider ?? 'Unknown'} · {status?.state ?? 'loading'}</p>
    </div>
    {canInitiate ? <form onSubmit={submit}>
      <label htmlFor="destinationNumber">Destination (E.164)</label>
      <label htmlFor="outboundLanguage">Starting language</label>
      <select id="outboundLanguage" value={languageCode} onChange={(event) => setLanguageCode(event.target.value)}>
        <option value="">Use location default{locationDefaultLanguageCode ? ` (${locationDefaultLanguageCode})` : ''}</option>
        {supportedLanguages.map((language) => <option key={language.code} value={language.code}>{language.displayName}</option>)}
      </select>
      <div className="field-row">
        <input id="destinationNumber" aria-invalid={destination.length > 0 && !valid}
          placeholder="+17875551234" value={destination} onChange={(event) => setDestination(event.target.value)} />
        <button type="submit" disabled={!valid || loading || status?.enabled !== true || status?.configured !== true}>
          {loading ? 'Requesting…' : 'Call'}
        </button>
      </div>
      {destination.length > 0 && !valid && <p className="error-text">Enter a valid number with + and country code.</p>}
      {error && <p className="error-text">The call could not be requested.</p>}
      {status?.enabled !== true && <p className="subtle">Telephony is safely disabled.</p>}
    </form> : <p className="subtle">Your role cannot initiate outbound calls.</p>}
  </section>
}

export function LocationLanguageControl({ supportedLanguages, value, currentValue, loading, error, success,
  onChange, onSave }: {
  supportedLanguages: SupportedCallLanguage[]
  value: string
  currentValue: string
  loading: boolean
  error: boolean
  success: boolean
  onChange(value: string): void
  onSave(): Promise<void>
}) {
  const supported = supportedLanguages.some((language) => language.code === value)
  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!value || !supported || value === currentValue) return
    await onSave()
  }
  return <form onSubmit={submit}>
    <label htmlFor="defaultCallLanguage">Default call language</label>
    <div className="field-row">
      <select id="defaultCallLanguage" value={value} aria-invalid={!supported}
        aria-describedby={!supported || error ? 'defaultCallLanguageError' : undefined}
        onChange={(event) => onChange(event.target.value)}>
        {value && !supported && <option value={value} disabled>Unsupported saved value</option>}
        {supportedLanguages.map((language) =>
          <option key={language.code} value={language.code}>{language.displayName}</option>)}
      </select>
      <button type="submit" disabled={loading || !supported || value === currentValue}>
        {loading ? 'Saving…' : 'Save language'}
      </button>
    </div>
    {!supported && <p id="defaultCallLanguageError" className="error-text">The saved language is not currently supported.</p>}
    {success && <p className="success">Default call language saved.</p>}
    {error && <p id={supported ? 'defaultCallLanguageError' : undefined} className="error-text">The language is unsupported or the setting changed. Refresh and try again.</p>}
  </form>
}

export function HangupControl({ canHangup, loading, error, onHangup }: {
  canHangup: boolean
  loading: boolean
  error: boolean
  onHangup(): void
}) {
  if (!canHangup) return null
  return <>
    <button className="hangup" type="button" disabled={loading} onClick={onHangup}>
      {loading ? 'Requesting…' : 'Hang up'}
    </button>
    {error && <p className="error-text">The hangup request could not be completed.</p>}
  </>
}
