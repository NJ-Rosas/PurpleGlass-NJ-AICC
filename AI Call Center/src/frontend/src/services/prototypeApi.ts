import { createApi, fetchBaseQuery } from '@reduxjs/toolkit/query/react'

export interface TenantSummary {
  tenantId: string
  tenantDisplayName: string
  locationId: string
  locationDisplayName: string
  timeZoneId: string
  version: number
}

export interface CallSummary {
  callId: string
  direction: string
  state: string
  createdAtUtc: string
  completedAtUtc?: string
  outcome?: string
  summary?: string
  recordingReference?: string
  version: number
}

export interface TranscriptTurn {
  turnId: string
  speaker: string
  sequenceNumber: number
  text: string
  createdAtUtc: string
  safetyFlagged: boolean
  escalationFlagged: boolean
}

export interface ConversationDetails {
  conversationId: string
  callId: string
  state: string
  language: string
  escalated: boolean
  escalationReason?: string
  transcript: TranscriptTurn[]
  summary?: {
    summary: string
    callerIntent?: string
    outcome: string
    followUpRequired: boolean
    escalated: boolean
    generatedAtUtc: string
  }
}

export interface CallDetails {
  call: CallSummary
  conversation?: ConversationDetails
}

export const prototypeApi = createApi({
  reducerPath: 'prototypeApi',
  baseQuery: fetchBaseQuery({ baseUrl: '/bff/v1' }),
  tagTypes: ['TenantSummary', 'Calls'],
  endpoints: (builder) => ({
    getTenantSummary: builder.query<TenantSummary, void>({
      query: () => '/tenant-summary',
      providesTags: ['TenantSummary'],
    }),
    updateLocationName: builder.mutation<TenantSummary, { locationId: string; displayName: string; expectedVersion: number }>({
      query: ({ locationId, ...body }) => ({
        url: `/locations/${locationId}/display-name`,
        method: 'PUT',
        body,
      }),
      invalidatesTags: ['TenantSummary'],
    }),
    getCalls: builder.query<CallSummary[], void>({
      query: () => '/calls?limit=20',
      providesTags: ['Calls'],
    }),
    getCallDetails: builder.query<CallDetails, string>({
      query: (callId) => `/calls/${callId}`,
      providesTags: ['Calls'],
    }),
  }),
})

export const {
  useGetTenantSummaryQuery,
  useUpdateLocationNameMutation,
  useGetCallsQuery,
  useGetCallDetailsQuery,
} = prototypeApi
