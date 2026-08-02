import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { HangupControl, isValidDestination, LocationLanguageControl, selectedLanguageCode, TelephonyControls } from './TelephonyControls'

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

  it('submits only normalized supported language values', () => {
    const languages = [{ code: 'en-US', displayName: 'English' }, { code: 'es-PR', displayName: 'Spanish (Puerto Rico)' }]
    expect(selectedLanguageCode('', languages)).toBeUndefined()
    expect(selectedLanguageCode('es-PR', languages)).toBe('es-PR')
    expect(selectedLanguageCode('spanish', languages)).toBeUndefined()
  })

  it('renders bounded outbound language choices and the location-default option accessibly', () => {
    const html = renderToStaticMarkup(<TelephonyControls
      status={{ provider: 'Twilio', enabled: true, configured: true, state: 'configured' }}
      canInitiate loading={false} error={false}
      supportedLanguages={[{ code: 'en-US', displayName: 'English' }, { code: 'es-PR', displayName: 'Spanish (Puerto Rico)' }]}
      locationDefaultLanguageCode="es-PR" onStart={vi.fn()} />)
    expect(html).toContain('id="outboundLanguage"')
    expect(html).toContain('Use location default (es-PR)')
    expect(html).toContain('value="en-US"')
    expect(html).toContain('value="es-PR"')
    expect(html).not.toContain('type="text" id="outboundLanguage"')
  })

  it('renders the location selector with loading, errors, and unsupported-state protection', () => {
    const languages = [{ code: 'en-US', displayName: 'English' }, { code: 'es-US', displayName: 'Spanish' }]
    const loading = renderToStaticMarkup(<LocationLanguageControl supportedLanguages={languages}
      value="es-US" currentValue="en-US" loading error success={false} onChange={vi.fn()} onSave={vi.fn()} />)
    expect(loading).toContain('Default call language')
    expect(loading).toContain('id="defaultCallLanguage"')
    expect(loading).toContain('Saving…')
    expect(loading).toContain('The language is unsupported or the setting changed')
    const unsupported = renderToStaticMarkup(<LocationLanguageControl supportedLanguages={languages}
      value="fr-FR" currentValue="fr-FR" loading={false} error={false} success={false}
      onChange={vi.fn()} onSave={vi.fn()} />)
    expect(unsupported).toContain('Unsupported saved value')
    expect(unsupported).toContain('aria-invalid="true"')
    expect(unsupported).toContain('The saved language is not currently supported')
  })

  it('shows hangup only for an authorized active call and reflects progress', () => {
    expect(renderToStaticMarkup(<HangupControl canHangup={false} loading={false} error={false} onHangup={vi.fn()} />)).toBe('')
    const active = renderToStaticMarkup(<HangupControl canHangup loading error onHangup={vi.fn()} />)
    expect(active).toContain('Requesting')
    expect(active).toContain('could not be completed')
  })
})
