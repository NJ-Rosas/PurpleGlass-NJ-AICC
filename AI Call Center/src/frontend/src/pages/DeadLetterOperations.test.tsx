import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { DeadLetterDetailPanel, DeadLetterTable } from './DeadLetterOperations'
import type { DeadLetterDetail, DeadLetterSummary } from '../services/prototypeApi'

const item: DeadLetterSummary = {
  messageId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
  tenantId: '11111111-1111-1111-1111-111111111111',
  locationId: '22222222-2222-2222-2222-222222222222',
  messageType: 'SyntheticEvent',
  status: 'DeadLetter',
  attemptCount: 5,
  createdAtUtc: '2026-07-25T20:00:00Z',
  deadLetteredAtUtc: '2026-07-25T21:00:00Z',
  correlationId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
  traceId: '4bf92f3577b34da6a3ce929d0e0e4736',
  failureCategory: 'Timeout',
  failureSummary: 'The operation timed out.',
  recoveryCount: 0,
}

describe('dead-letter operations', () => {
  it('renders rows and the empty state', () => {
    expect(renderToStaticMarkup(<DeadLetterTable items={[item]} />)).toContain('SyntheticEvent')
    expect(renderToStaticMarkup(<DeadLetterTable items={[]} />)).toContain('No dead-lettered operations')
  })

  it('renders safe detail metadata without a payload field', () => {
    const detail: DeadLetterDetail = { message: item, producer: 'purpleglass-platform', dataClassification: 'internal', recoverable: true }
    const html = renderToStaticMarkup(<DeadLetterDetailPanel detail={detail} canRecover onRetry={vi.fn()} retrying={false} />)
    expect(html).toContain('Failure category')
    expect(html).toContain(item.correlationId)
    expect(html).toContain('Retry')
    expect(html).not.toMatch(/payload|transcript|prompt|token|credential/i)
  })

  it('hides recovery control from unauthorized users and shows feedback', () => {
    const detail: DeadLetterDetail = { message: item, producer: 'purpleglass-platform', dataClassification: 'internal', recoverable: true }
    const html = renderToStaticMarkup(<DeadLetterDetailPanel detail={detail} canRecover={false} onRetry={vi.fn()} retrying={false} feedback="Recovery could not be requested." />)
    expect(html).not.toContain('>Retry<')
    expect(html).toContain('Recovery could not be requested.')
  })
})
