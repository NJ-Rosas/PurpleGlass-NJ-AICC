import type { CallDetails, TranscriptTurn } from '../services/prototypeApi'
import { HangupControl } from './TelephonyControls'

export type VoiceState = 'Listening' | 'Thinking' | 'Speaking' | 'Interrupted' | 'Failed'

export interface VoiceStateUpdate {
  callId: string
  state: VoiceState
  safeCode?: string
}

export type VoiceStatesByCall = Record<string, VoiceStateUpdate>

export function withVoiceState(
  current: VoiceStatesByCall,
  update: VoiceStateUpdate,
): VoiceStatesByCall {
  return { ...current, [update.callId]: update }
}

export function withoutVoiceState(
  current: VoiceStatesByCall,
  callId: string,
): VoiceStatesByCall {
  if (!current[callId]) return current
  const next = { ...current }
  delete next[callId]
  return next
}

const callIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i
const safeCodePattern = /^[a-z0-9_.-]{1,80}$/i
const voiceStates: Record<string, VoiceState> = {
  listening: 'Listening',
  thinking: 'Thinking',
  speaking: 'Speaking',
  interrupted: 'Interrupted',
  failed: 'Failed',
}

export function parseVoiceStateEvent(data: string): VoiceStateUpdate | undefined {
  try {
    const value: unknown = JSON.parse(data)
    if (!value || typeof value !== 'object') return undefined
    const candidate = value as Record<string, unknown>
    if (typeof candidate.callId !== 'string' || !callIdPattern.test(candidate.callId)) return undefined
    if (typeof candidate.state !== 'string') return undefined
    const state = voiceStates[candidate.state.toLowerCase()]
    if (!state) return undefined
    const safeCode = typeof candidate.safeCode === 'string' && safeCodePattern.test(candidate.safeCode)
      ? candidate.safeCode
      : undefined
    return { callId: candidate.callId, state, ...(safeCode ? { safeCode } : {}) }
  } catch {
    return undefined
  }
}

export function isTerminalCallState(state: string): boolean {
  return state === 'Completed' || state === 'Failed'
}

function isConnectedCallState(state: string): boolean {
  return state === 'Answered' || state === 'InConversation'
}

function callStateLabel(state: string): string {
  return isConnectedCallState(state) ? 'Connected' : state
}

function speakerPresentation(turn: TranscriptTurn): { className: string; label: string } {
  const speaker = turn.speaker.toLowerCase()
  if (speaker === 'assistant' || speaker === 'ai') return { className: 'assistant', label: 'AI' }
  if (speaker === 'caller') return { className: 'caller', label: 'Caller' }
  return { className: 'participant', label: 'Participant' }
}

function stateMessage(state: VoiceState): string | undefined {
  if (state === 'Interrupted') return 'AI playback stopped. Listening for the caller.'
  if (state === 'Failed') return 'Voice processing is temporarily unavailable.'
  return undefined
}

export interface VoiceCallDetailProps {
  details: CallDetails
  voiceState?: VoiceStateUpdate
  canHangup: boolean
  hangupLoading: boolean
  hangupError: boolean
  onHangup(): void
}

export function VoiceCallDetail({
  details,
  voiceState,
  canHangup,
  hangupLoading,
  hangupError,
  onHangup,
}: VoiceCallDetailProps) {
  const connected = isConnectedCallState(details.call.state)
  const terminal = isTerminalCallState(details.call.state)
  const visibleVoiceState = connected && voiceState?.callId === details.call.callId
    ? voiceState
    : undefined
  const currentVoiceState = visibleVoiceState?.state ?? 'Listening'
  const transcript = [...(details.conversation?.transcript ?? [])]
    .sort((left, right) => left.sequenceNumber - right.sequenceNumber
      || left.createdAtUtc.localeCompare(right.createdAtUtc)
      || left.turnId.localeCompare(right.turnId))

  return <>
    <p className="eyebrow">{connected ? 'LIVE CALL' : 'CALL REVIEW'}</p>
    <div className="detail-title">
      <div>
        <h3>{details.call.direction} call</h3>
        <p className="subtle">{details.call.outcome ?? callStateLabel(details.call.state)}</p>
        <p className="subtle">{details.call.provider} · {details.call.fromNumber} → {details.call.toNumber}</p>
      </div>
      <span className={`call-state ${details.call.state.toLowerCase()}`}>{callStateLabel(details.call.state)}</span>
    </div>

    {connected && <div className={`voice-state voice-state-${currentVoiceState.toLowerCase()}`} aria-live="polite">
      <span>AI State</span>
      <strong>{currentVoiceState}</strong>
      {stateMessage(currentVoiceState) && <p role={currentVoiceState === 'Failed' ? 'alert' : 'status'}>
        {stateMessage(currentVoiceState)}
      </p>}
      {currentVoiceState === 'Failed' && visibleVoiceState?.safeCode &&
        <small>Reference code: {visibleVoiceState.safeCode}</small>}
    </div>}

    <HangupControl canHangup={canHangup && !terminal}
      loading={hangupLoading} error={hangupError} onHangup={onHangup} />

    {details.conversation?.summary && (
      <div className="summary-card">
        <strong>AI summary</strong>
        <p>{details.conversation.summary.summary}</p>
      </div>
    )}
    {details.conversation?.escalated && (
      <div className="escalation">Escalated: {details.conversation.escalationReason ?? 'review required'}</div>
    )}
    <div className="transcript" aria-label="Call transcript">
      {transcript.map((turn) => {
        const speaker = speakerPresentation(turn)
        return <div key={turn.turnId} className={`turn ${speaker.className}`}>
          <span>{speaker.label}</span>
          <p>{turn.text}</p>
        </div>
      })}
      {details.conversation && transcript.length === 0 &&
        <div className="empty-state">No finalized transcript turns yet.</div>}
      {!details.conversation && <div className="empty-state">No conversation was recorded for this call.</div>}
    </div>
  </>
}
