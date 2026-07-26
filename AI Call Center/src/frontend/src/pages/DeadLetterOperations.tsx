import { useState } from 'react'
import {
  DeadLetterDetail,
  DeadLetterSummary,
  useGetDeadLetterQuery,
  useGetDeadLettersQuery,
  useRetryDeadLetterMutation,
} from '../services/prototypeApi'

export interface DeadLetterTableProps {
  items: DeadLetterSummary[]
  selectedId?: string
  onSelect?: (id: string) => void
}

export function DeadLetterTable({ items, selectedId, onSelect }: DeadLetterTableProps) {
  if (items.length === 0) return <div className="empty-state">No dead-lettered operations match these filters.</div>
  return <div className="dead-letter-items" aria-label="Dead-lettered operations">
    {items.map((item) => <button type="button" key={item.messageId}
      className={selectedId === item.messageId ? 'dead-letter-row selected' : 'dead-letter-row'}
      onClick={() => onSelect?.(item.messageId)}>
      <strong>{item.messageType}</strong>
      <span>{item.failureCategory}</span>
      <span>{item.attemptCount} attempts</span>
      <time>{new Date(item.deadLetteredAtUtc ?? item.lastAttemptAtUtc ?? item.createdAtUtc).toLocaleString()}</time>
    </button>)}
  </div>
}

export function DeadLetterDetailPanel({ detail, canRecover, onRetry, retrying, feedback }: {
  detail?: DeadLetterDetail; canRecover: boolean; onRetry: () => void; retrying: boolean; feedback?: string
}) {
  if (!detail) return <div className="empty-state">Select a failed operation to inspect safe metadata.</div>
  const item = detail.message
  return <div className="dead-letter-detail">
    <h3>{item.messageType}</h3>
    <dl>
      <dt>Message</dt><dd>{item.messageId}</dd>
      <dt>Status</dt><dd>{item.status}</dd>
      <dt>Created</dt><dd>{new Date(item.createdAtUtc).toLocaleString()}</dd>
      <dt>Dead-lettered</dt><dd>{item.deadLetteredAtUtc ? new Date(item.deadLetteredAtUtc).toLocaleString() : 'Unknown'}</dd>
      <dt>Attempts</dt><dd>{item.attemptCount}</dd>
      <dt>Failure category</dt><dd>{item.failureCategory}</dd>
      <dt>Failure summary</dt><dd>{item.failureSummary}</dd>
      <dt>Correlation ID</dt><dd>{item.correlationId}</dd>
      <dt>Trace ID</dt><dd>{item.traceId ?? 'Unavailable'}</dd>
      <dt>Recoveries</dt><dd>{item.recoveryCount}</dd>
    </dl>
    {canRecover && detail.recoverable && <button type="button" onClick={onRetry} disabled={retrying}>
      {retrying ? 'Retrying…' : 'Retry'}
    </button>}
    {feedback && <p role="status" className={feedback === 'Recovery requested.' ? 'success' : 'error-text'}>{feedback}</p>}
  </div>
}

export function DeadLetterOperations({ permissions }: { permissions: string[] }) {
  const [page, setPage] = useState(1)
  const [messageType, setMessageType] = useState('')
  const [failureCategory, setFailureCategory] = useState('')
  const [selectedId, setSelectedId] = useState<string>()
  const [feedback, setFeedback] = useState<string>()
  const canView = permissions.includes('operations.deadletters.view')
  const canRecover = permissions.includes('operations.deadletters.recover')
  const list = useGetDeadLettersQuery({ page, pageSize: 10, messageType: messageType || undefined, failureCategory: failureCategory || undefined }, { skip: !canView })
  const detail = useGetDeadLetterQuery(selectedId ?? '', { skip: !selectedId || !canView })
  const [retry, retryState] = useRetryDeadLetterMutation()

  if (!canView) return <section className="panel"><p className="error-text">You are not authorized to view dead-letter operations.</p></section>
  async function requestRetry() {
    if (!selectedId || !window.confirm('Retry this failed operation? It will return to the normal processing pipeline.')) return
    try {
      await retry(selectedId).unwrap()
      setFeedback('Recovery requested.')
    } catch {
      setFeedback('Recovery could not be requested. Refresh and review its current state.')
    }
  }

  return <section className="operations-layout">
    <div className="panel">
      <div className="section-heading"><div><p className="eyebrow">OPERATIONS</p><h2>Dead Letters</h2></div><button onClick={() => list.refetch()}>Refresh</button></div>
      <div className="dead-letter-filters">
        <label>Type<input value={messageType} onChange={(event) => { setMessageType(event.target.value); setPage(1) }} /></label>
        <label>Failure<select value={failureCategory} onChange={(event) => { setFailureCategory(event.target.value); setPage(1) }}>
          <option value="">All</option><option>Timeout</option><option>DependencyUnavailable</option><option>Validation</option><option>Unhandled</option>
        </select></label>
      </div>
      {list.isLoading && <p className="subtle">Loading dead letters…</p>}
      {list.isError && <p className="error-text">Dead-letter operations are unavailable.</p>}
      <DeadLetterTable items={list.data?.items ?? []} selectedId={selectedId} onSelect={setSelectedId} />
      <div className="pagination"><button disabled={page === 1} onClick={() => setPage(page - 1)}>Previous</button><span>Page {page}</span><button disabled={!list.data || page * list.data.pageSize >= list.data.totalCount} onClick={() => setPage(page + 1)}>Next</button></div>
    </div>
    <div className="panel"><p className="eyebrow">SAFE FAILURE DETAIL</p>
      {detail.isLoading ? <p className="subtle">Loading details…</p> : <DeadLetterDetailPanel detail={detail.data} canRecover={canRecover} onRetry={requestRetry} retrying={retryState.isLoading} feedback={feedback} />}
    </div>
  </section>
}
