import { createSlice, PayloadAction } from '@reduxjs/toolkit'

export interface SessionState {
  status: 'loading' | 'authenticated' | 'unauthenticated' | 'denied'
  userId?: string
  displayName?: string
}

const initialState: SessionState = { status: 'loading' }

const sessionSlice = createSlice({
  name: 'session',
  initialState,
  reducers: {
    authenticated: (state, action: PayloadAction<{ userId: string; displayName: string }>) => {
      state.status = 'authenticated'
      state.userId = action.payload.userId
      state.displayName = action.payload.displayName
    },
    unauthenticated: () => ({ status: 'unauthenticated' as const }),
    accessDenied: (state) => { state.status = 'denied' },
    resetSession: () => initialState,
  },
})

export const { authenticated, unauthenticated, accessDenied, resetSession } = sessionSlice.actions
export const sessionReducer = sessionSlice.reducer
