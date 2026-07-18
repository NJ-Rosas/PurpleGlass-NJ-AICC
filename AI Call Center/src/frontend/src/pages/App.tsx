import { FormEvent, useEffect, useState } from 'react'
import { prototypeApi, useGetTenantSummaryQuery, useUpdateLocationNameMutation } from '../services/prototypeApi'
import { store } from '../app/store'

export function App() {
  const { data, isLoading, isError } = useGetTenantSummaryQuery()
  const [updateName, updateState] = useUpdateLocationNameMutation()
  const [name, setName] = useState('')
  const [realtime, setRealtime] = useState<'connecting' | 'live' | 'offline'>('connecting')

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
            <article className="metric"><span>Calls today</span><strong>0</strong><small>Voice integration is next</small></article>
            <article className="metric"><span>Appointments</span><strong>—</strong><small>Awaiting Open Dental adapter</small></article>
            <article className="metric"><span>Response time</span><strong>Live</strong><small>MQTT + SSE connected</small></article>
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
