import { FormEvent, useEffect, useState } from 'react'
import {
  prototypeApi,
  clearCsrfToken,
  useDevelopmentLoginMutation,
  useGetCallDetailsQuery,
  useGetCallsQuery,
  useGetSessionQuery,
  useGetTenantSummaryQuery,
  useLogoutMutation,
  useUpdateLocationNameMutation,
} from '../services/prototypeApi'
import { store } from '../app/store'
import { reportOperationalFailure } from '../services/operationalDiagnostics'
import { DeadLetterOperations } from './DeadLetterOperations'

export function App() {
  const { data: session, isLoading: sessionLoading, isError: sessionError, refetch: refetchSession } = useGetSessionQuery()
  const [developmentLogin, loginState] = useDevelopmentLoginMutation()
  const [logout] = useLogoutMutation()
  const { data, isLoading, isError } = useGetTenantSummaryQuery(undefined, { skip: !session })
  const [updateName, updateState] = useUpdateLocationNameMutation()
  const [name, setName] = useState('')
  const [realtime, setRealtime] = useState<'connecting' | 'live' | 'offline'>('connecting')
  const { data: calls = [], isLoading: callsLoading, isError: callsError } = useGetCallsQuery(undefined, { skip: !session })
  const [selectedCallId, setSelectedCallId] = useState<string>()
  const [view, setView] = useState<'dashboard' | 'dead-letters'>('dashboard')
  const selectedCall = selectedCallId ?? calls[0]?.callId
  const { data: callDetails, isLoading: detailsLoading } = useGetCallDetailsQuery(selectedCall ?? '', {
    skip: !selectedCall,
  })

  useEffect(() => {
    if (data) setName(data.locationDisplayName)
  }, [data])

  useEffect(() => {
    if (!session) return
    const events = new EventSource('/bff/v1/events')
    events.onopen = () => setRealtime('live')
    events.onerror = () => {
      setRealtime('offline')
      reportOperationalFailure('realtime.connection_failed')
    }
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
    events.addEventListener('dead-letter-recovered', () => store.dispatch(prototypeApi.util.invalidateTags(['DeadLetters'])))
    return () => events.close()
  }, [session])

  async function login(user: 'administrator' | 'read-only') {
    await developmentLogin(user).unwrap()
    await refetchSession()
  }

  async function endSession() {
    await logout().unwrap()
    clearCsrfToken()
    store.dispatch(prototypeApi.util.resetApiState())
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!data || !name.trim()) return
    await updateName({ locationId: data.locationId, displayName: name.trim(), expectedVersion: data.version })
  }

  if (sessionLoading) return <main className="auth-shell"><section className="panel">Checking your secure session…</section></main>
  if (sessionError || !session) return (
    <main className="auth-shell">
      <section className="panel auth-card">
        <p className="eyebrow">DEVELOPMENT AUTHENTICATION</p>
        <h1>Sign in to PurpleGlass</h1>
        <p className="subtle">Synthetic identities only. Never enter patient information.</p>
        <button onClick={() => login('administrator')} disabled={loginState.isLoading}>Sign in as administrator</button>
        <button onClick={() => login('read-only')} disabled={loginState.isLoading}>Sign in as read-only user</button>
        {loginState.isError && <p className="error-text">The development sign-in was rejected.</p>}
      </section>
    </main>
  )

  return (
    <div className="shell">
      <aside className="rail">
        <div className="mark">PG</div>
        <nav aria-label="Primary">
          <button className={view === 'dashboard' ? 'nav-active' : ''} aria-label="Dashboard" onClick={() => setView('dashboard')}>⌂</button>
          <button aria-label="Calls">☏</button>
          {session.permissions.includes('operations.deadletters.view') && <button className={view === 'dead-letters' ? 'nav-active' : ''} aria-label="Dead letters" onClick={() => setView('dead-letters')}>!</button>}
          <button aria-label="Settings">⚙</button>
        </nav>
        <button className="avatar" onClick={endSession} aria-label="Log out">{session.displayName.slice(0, 2).toUpperCase()}</button>
      </aside>

      <main>
        <header>
          <div>
            <p className="eyebrow">AI CALL CENTER</p>
            <h1>Welcome, {session.displayName}</h1>
            <p className="subtle">{session.role} · secure {session.authenticationMethod} session</p>
          </div>
          <div className={`status ${realtime}`}><span /> {realtime === 'live' ? 'Realtime connected' : realtime}</div>
        </header>

        {view === 'dead-letters' && <DeadLetterOperations permissions={session.permissions} />}
        {view === 'dashboard' && isLoading && <section className="panel">Loading the prototype workspace…</section>}
        {isError && <section className="panel error">The BFF is unavailable. Start the local backend and refresh.</section>}

        {view === 'dashboard' && data && <>
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
