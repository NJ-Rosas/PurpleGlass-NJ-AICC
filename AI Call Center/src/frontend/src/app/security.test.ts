import { describe, expect, it } from 'vitest'
import { authenticated, sessionReducer, unauthenticated } from './sessionSlice'
import { isMutation } from '../services/prototypeApi'

describe('browser security state', () => {
  it('tracks only the sanitized session projection', () => {
    const state = sessionReducer(undefined, authenticated({ userId: 'opaque-user', displayName: 'Synthetic User' }))
    expect(state).toEqual({ status: 'authenticated', userId: 'opaque-user', displayName: 'Synthetic User' })
    expect(JSON.stringify(state)).not.toMatch(/token|cookie|secret/i)
  })

  it('clears user projection on logout', () => {
    const authenticatedState = sessionReducer(undefined, authenticated({ userId: 'user', displayName: 'User' }))
    expect(sessionReducer(authenticatedState, unauthenticated())).toEqual({ status: 'unauthenticated' })
  })

  it('requires antiforgery handling only for unsafe methods', () => {
    expect(isMutation('/session')).toBe(false)
    expect(isMutation({ url: '/logout', method: 'POST' })).toBe(true)
    expect(isMutation({ url: '/locations/id', method: 'PUT' })).toBe(true)
    expect(isMutation({ url: '/calls', method: 'GET' })).toBe(false)
  })
})
