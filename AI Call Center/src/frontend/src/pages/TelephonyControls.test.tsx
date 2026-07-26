import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { HangupControl, isValidDestination, TelephonyControls } from './TelephonyControls'

describe('telephony controls', () => {
  it('validates E.164 destinations', () => {
    expect(isValidDestination('+17875551234')).toBe(true)
    expect(isValidDestination('787-555-1234')).toBe(false)
    expect(isValidDestination('+012345678')).toBe(false)
  })

  it('renders configured transport and hides the form without permission', () => {
    const status = { provider: 'Twilio', enabled: true, configured: true, state: 'configured' }
    const authorized = renderToStaticMarkup(<TelephonyControls status={status} canInitiate loading={false} error={false} onStart={vi.fn()} />)
    expect(authorized).toContain('Start test call')
    expect(authorized).toContain('Twilio')
    const unauthorized = renderToStaticMarkup(<TelephonyControls status={status} canInitiate={false} loading={false} error={false} onStart={vi.fn()} />)
    expect(unauthorized).not.toContain('destinationNumber')
    expect(unauthorized).toContain('cannot initiate')
  })

  it('shows disabled and failure feedback safely', () => {
    const html = renderToStaticMarkup(<TelephonyControls status={{ provider: 'None', enabled: false, configured: false, state: 'disabled' }} canInitiate loading={false} error onStart={vi.fn()} />)
    expect(html).toContain('safely disabled')
    expect(html).toContain('could not be requested')
  })

  it('shows hangup only for an authorized active call and reflects progress', () => {
    expect(renderToStaticMarkup(<HangupControl canHangup={false} loading={false} error={false} onHangup={vi.fn()} />)).toBe('')
    const active = renderToStaticMarkup(<HangupControl canHangup loading error onHangup={vi.fn()} />)
    expect(active).toContain('Requesting')
    expect(active).toContain('could not be completed')
  })
})
