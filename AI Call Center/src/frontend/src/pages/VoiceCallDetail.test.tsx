import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import type { CallDetails, TranscriptTurn } from '../services/prototypeApi'
import {
  parseVoiceStateEvent,
  VoiceCallDetail,
  withVoiceState,
  withoutVoiceState,
  type VoiceState,
  type VoiceStateUpdate,
} from './VoiceCallDetail'

const callId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'

function turn(sequenceNumber: number, speaker: string, text: string): TranscriptTurn {
  return {
    turnId: `${sequenceNumber}0000000-0000-4000-8000-000000000000`,
    speaker,
    sequenceNumber,
    text,
    createdAtUtc: `2026-07-26T12:00:0${sequenceNumber}Z`,
    safetyFlagged: false,
    escalationFlagged: false,
  }
}

function details(state = 'InConversation', transcript: TranscriptTurn[] = []): CallDetails {
  return {
    call: {
      callId,
      direction: 'Inbound',
      state,
      createdAtUtc: '2026-07-26T12:00:00Z',
      version: 3,
      provider: 'Fake',
      fromNumber: '+17875550101',
      toNumber: '+17875550202',
    },
    conversation: {
      conversationId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
      callId,
      state: 'Active',
      language: 'en-US',
      escalated: false,
      transcript,
    },
  }
}

function render(state: VoiceState, overrides: Partial<VoiceStateUpdate> = {}): string {
  return renderToStaticMarkup(<VoiceCallDetail details={details()}
    voiceState={{ callId, state, ...overrides }} canHangup hangupLoading={false}
    hangupError={false} onHangup={vi.fn()} />)
}

describe('realtime voice call detail', () => {
  it('renders an active call as connected', () => {
    const html = render('Listening')
    expect(html).toContain('LIVE CALL')
    expect(html).toContain('Connected')
  })

  it('orders finalized transcript turns and maps Assistant attribution to AI', () => {
    const html = renderToStaticMarkup(<VoiceCallDetail
      details={details('InConversation', [turn(2, 'Assistant', 'Second response'), turn(1, 'Caller', 'First question')])}
      canHangup hangupLoading={false} hangupError={false} onHangup={vi.fn()} />)
    expect(html.indexOf('First question')).toBeLessThan(html.indexOf('Second response'))
    expect(html).toContain('>Caller<')
    expect(html).toContain('>AI<')
    expect(html).not.toContain('>Assistant<')
  })

  it('renders listening state', () => expect(render('Listening')).toContain('>Listening<'))

  it('renders thinking state', () => expect(render('Thinking')).toContain('>Thinking<'))

  it('renders speaking state', () => expect(render('Speaking')).toContain('>Speaking<'))

  it('renders interruption state with playback feedback', () => {
    const html = render('Interrupted')
    expect(html).toContain('>Interrupted<')
    expect(html).toContain('AI playback stopped')
  })

  it('renders a bounded failure message and safe reference code', () => {
    const html = render('Failed', { safeCode: 'llm_timeout' })
    expect(html).toContain('temporarily unavailable')
    expect(html).toContain('llm_timeout')
    expect(html).not.toMatch(/exception|stack|prompt/i)
  })

  it('retains permission-aware hangup behavior for active calls', () => {
    const authorized = renderToStaticMarkup(<VoiceCallDetail details={details()} canHangup
      hangupLoading hangupError onHangup={vi.fn()} />)
    expect(authorized).toContain('Requesting')
    expect(authorized).toContain('hangup request could not be completed')
    const unauthorized = renderToStaticMarkup(<VoiceCallDetail details={details()} canHangup={false}
      hangupLoading={false} hangupError={false} onHangup={vi.fn()} />)
    expect(unauthorized).not.toContain('Hang up')
    const completed = renderToStaticMarkup(<VoiceCallDetail details={details('Completed')}
      voiceState={{ callId, state: 'Speaking' }} canHangup hangupLoading={false}
      hangupError={false} onHangup={vi.fn()} />)
    expect(completed).not.toContain('AI State')
    expect(completed).not.toContain('Hang up')
  })

  it('accepts only sanitized voice-state events', () => {
    expect(parseVoiceStateEvent(JSON.stringify({ callId, state: 'thinking', safeCode: 'provider_timeout' })))
      .toEqual({ callId, state: 'Thinking', safeCode: 'provider_timeout' })
    expect(parseVoiceStateEvent(JSON.stringify({ callId: 'unknown', state: 'Listening' }))).toBeUndefined()
    expect(parseVoiceStateEvent(JSON.stringify({ callId, state: 'LeakingSecrets' }))).toBeUndefined()
    expect(parseVoiceStateEvent(JSON.stringify({ callId, state: 'Failed', safeCode: 'raw exception text' })))
      .toEqual({ callId, state: 'Failed' })
    expect(parseVoiceStateEvent('not-json')).toBeUndefined()
  })

  it('keeps transient voice state isolated per active call', () => {
    const otherCallId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
    let states = withVoiceState({}, { callId, state: 'Thinking' })
    states = withVoiceState(states, { callId: otherCallId, state: 'Speaking' })

    expect(states[callId]?.state).toBe('Thinking')
    expect(states[otherCallId]?.state).toBe('Speaking')
    states = withoutVoiceState(states, otherCallId)
    expect(states[callId]?.state).toBe('Thinking')
    expect(states[otherCallId]).toBeUndefined()
  })
})
