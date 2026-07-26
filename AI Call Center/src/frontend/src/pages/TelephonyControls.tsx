import { FormEvent, useState } from 'react'
import type { TelephonyStatus } from '../services/prototypeApi'

const e164 = /^\+[1-9][0-9]{7,14}$/

export function isValidDestination(value: string): boolean { return e164.test(value.trim()) }

export interface TelephonyControlsProps {
  status?: TelephonyStatus
  canInitiate: boolean
  loading: boolean
  error: boolean
  onStart(destination: string): Promise<void>
}

export function TelephonyControls({ status, canInitiate, loading, error, onStart }: TelephonyControlsProps) {
  const [destination, setDestination] = useState('')
  const valid = isValidDestination(destination)
  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!valid || !canInitiate) return
    await onStart(destination.trim())
  }

  return <section className="panel telephony-control" aria-label="Telephony controls">
    <div>
      <p className="eyebrow">REAL CALL TRANSPORT</p>
      <h3>Start test call</h3>
      <p className="subtle">Provider: {status?.provider ?? 'Unknown'} · {status?.state ?? 'loading'}</p>
    </div>
    {canInitiate ? <form onSubmit={submit}>
      <label htmlFor="destinationNumber">Destination (E.164)</label>
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
