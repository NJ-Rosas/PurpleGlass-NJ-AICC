import { FormEvent, useEffect, useState } from 'react'
import {
  prototypeApi,
  useGetCallDetailsQuery,
  useGetCallsQuery,
  useGetTenantSummaryQuery,
  useUpdateLocationNameMutation,
} from '../services/prototypeApi'
import { store } from '../app/store'

export function App() {
  const { data, isLoading, isError } = useGetTenantSummaryQuery()
  const [updateName, updateState] = useUpdateLocationNameMutation()
  const [name, setName] = useState('')
  const [realtime, setRealtime] = useState<'connecting' | 'live' | 'offline'>('connecting')
  const { data: calls = [], isLoading: callsLoading, isError: callsError } = useGetCallsQuery()
  const [selectedCallId, setSelectedCallId] = useState<string>()
  const selectedCall = selectedCallId ?? calls[0]?.callId
  const { data: callDetails, isLoading: detailsLoading } = useGetCallDetailsQuery(selectedCall ?? '', {
    skip: !selectedCall,
  })

  useEffect(() => {
    if (data) setName(data.locationDisplayName)
  }, [data])

  useEffect(() => {
    const events = new EventSource('/bff/v1/events')
    events.onopen = () => setRealtime('live')
    events.onerror = () => setRealtime('offline')
    events.addEventListener('location-display-name-changed', () => {
      store.dispatch(prototypeApi.util.invalidateTags(['TenantSummary']))
    })
    const refreshCalls = () => store.dispatch(prototypeApi.util.invalidateTags(['Calls']))
    const callEventTypes = [
      'call-received',
      'call-state-changed',
      'call-completed',
      'call-failed',
      'conversation-started',
      'user-speech-recognized',
      'ai-response-generated',
      'summary-generated',
      'conversation-completed',
    ]
    callEventTypes.forEach((eventType) => events.addEventListener(eventType, refreshCalls))
    return () => events.close()
  }, [])

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!data || !name.trim()) return
    await updateName({ locationId: data.locationId, displayName: name.trim(), expectedVersion: data.version })
  }

  return (
    <div className="shell">
      <aside className="rail">
        <div className="mark">PG</div>
        <nav aria-label="Primary">
          <button className="nav-active" aria-label="Dashboard">⌂</button>
          <button aria-label="Calls">☏</button>
          <button aria-label="Patients">♙</button>
          <button aria-label="Settings">⚙</button>
        </nav>
        <div className="avatar">NI</div>
      </aside>

      <main>
        <header>
          <div>
            <p className="eyebrow">AI CALL CENTER</p>
            <h1>Good evening, Nilve</h1>
            <p className="subtle">Your dental office command center is ready.</p>
          </div>
          <div className={`status ${realtime}`}><span /> {realtime === 'live' ? 'Realtime connected' : realtime}</div>
        </header>

        {isLoading && <section className="panel">Loading the prototype workspace…</section>}
        {isError && <section className="panel error">The BFF is unavailable. Start the local backend and refresh.</section>}

        {data && <>
          <section className="hero-card">
            <div>
              <p className="eyebrow">ACTIVE PRACTICE</p>
              <h2>{data.tenantDisplayName}</h2>
              <p>{data.locationDisplayName} · {data.timeZoneId}</p>
            </div>
            <div className="version">DATA VERSION <strong>{data.version}</strong></div>
          </section>

          <section className="grid">
            <article className="metric"><span>Recent calls</span><strong>{calls.length}</strong><small>Durable simulated activity</small></article>
            <article className="metric"><span>Appointments</span><strong>—</strong><small>Awaiting Open Dental adapter</small></article>
            <article className="metric"><span>Response time</span><strong>Live</strong><small>MQTT + SSE connected</small></article>
          </section>

          <section className="calls-layout">
            <div className="panel call-list">
              <div className="section-heading">
                <div>
                  <p className="eyebrow">DURABLE ACTIVITY</p>
                  <h3>Recent calls</h3>
                </div>
                <span>{calls.length} loaded</span>
              </div>
              {callsLoading && <p className="subtle">Loading calls…</p>}
              {callsError && <p className="error-text">Call activity is unavailable.</p>}
              {!callsLoading && calls.length === 0 && (
                <div className="empty-state">Run the inbound or outbound simulator to create a synthetic call.</div>
              )}
              <div className="call-items">
                {calls.map((call) => (
                  <button
                    type="button"
                    key={call.callId}
                    className={selectedCall === call.callId ? 'call-item selected' : 'call-item'}
                    onClick={() => setSelectedCallId(call.callId)}
                  >
                    <span className={`call-direction ${call.direction.toLowerCase()}`}>{call.direction.slice(0, 1)}</span>
                    <span>
                      <strong>{call.direction} call</strong>
                      <small>{new Date(call.createdAtUtc).toLocaleString()}</small>
                    </span>
                    <span className={`call-state ${call.state.toLowerCase()}`}>{call.state}</span>
                  </button>
                ))}
              </div>
            </div>

            <div className="panel call-detail">
              <p className="eyebrow">CALL REVIEW</p>
              {!selectedCall && <div className="empty-state">Select a call to review its transcript.</div>}
              {detailsLoading && <p className="subtle">Loading transcript…</p>}
              {callDetails && (
                <>
                  <div className="detail-title">
                    <div>
                      <h3>{callDetails.call.direction} call</h3>
                      <p className="subtle">{callDetails.call.outcome ?? callDetails.call.state}</p>
                    </div>
                    <span className={`call-state ${callDetails.call.state.toLowerCase()}`}>{callDetails.call.state}</span>
                  </div>
                  {callDetails.conversation?.summary && (
                    <div className="summary-card">
                      <strong>AI summary</strong>
                      <p>{callDetails.conversation.summary.summary}</p>
                    </div>
                  )}
                  {callDetails.conversation?.escalated && (
                    <div className="escalation">Escalated: {callDetails.conversation.escalationReason ?? 'review required'}</div>
                  )}
                  <div className="transcript" aria-label="Call transcript">
                    {callDetails.conversation?.transcript.map((turn) => (
                      <div key={turn.turnId} className={`turn ${turn.speaker.toLowerCase()}`}>
                        <span>{turn.speaker}</span>
                        <p>{turn.text}</p>
                      </div>
                    ))}
                    {!callDetails.conversation && <div className="empty-state">No conversation was recorded for this call.</div>}
                  </div>
                </>
              )}
            </div>
          </section>

          <section className="panel editor">
            <div>
              <p className="eyebrow">PROTOTYPE CONTROL</p>
              <h3>Office identity</h3>
              <p className="subtle">This change travels React → Redux → BFF → PostgreSQL → outbox → MQTT → SSE.</p>
            </div>
            <form onSubmit={submit}>
              <label htmlFor="officeName">Display name</label>
              <div className="field-row">
                <input id="officeName" value={name} onChange={(event) => setName(event.target.value)} maxLength={160} />
                <button type="submit" disabled={updateState.isLoading || name.trim() === data.locationDisplayName}>
                  {updateState.isLoading ? 'Saving…' : 'Save change'}
                </button>
              </div>
              {updateState.isSuccess && <p className="success">Saved and queued for realtime delivery.</p>}
              {updateState.isError && <p className="error-text">The update conflicted or could not be saved. Refresh and try again.</p>}
            </form>
          </section>
        </>}
      </main>
    </div>
  )
}
