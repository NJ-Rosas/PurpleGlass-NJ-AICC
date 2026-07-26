import { configureStore } from '@reduxjs/toolkit'
import { prototypeApi } from '../services/prototypeApi'
import { sessionReducer } from './sessionSlice'

export const store = configureStore({
  reducer: { session: sessionReducer, [prototypeApi.reducerPath]: prototypeApi.reducer },
  middleware: (getDefaultMiddleware) => getDefaultMiddleware().concat(prototypeApi.middleware),
})

export type RootState = ReturnType<typeof store.getState>
export type AppDispatch = typeof store.dispatch
