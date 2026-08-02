import { BaseQueryFn, FetchArgs, FetchBaseQueryError, createApi, fetchBaseQuery } from '@reduxjs/toolkit/query/react'
import { reportOperationalFailure } from './operationalDiagnostics'

export interface SessionProjection {
  userId: string
  displayName: string
  tenantId: string
  activeLocationId: string
  authorizedLocationIds: string[]
  role: string
  permissions: string[]
  authenticationMethod: string
  developmentAuthentication: boolean
}

export interface TenantSummary {
  tenantId: string
  tenantDisplayName: string
  locationId: string
  locationDisplayName: string
  timeZoneId: string
  defaultCallLanguageCode: string
  supportedCallLanguages: SupportedCallLanguage[]
  version: number
}

export interface SupportedCallLanguage { code: string; displayName: string }

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
  provider: string
  fromNumber: string
  toNumber: string
}

export interface TelephonyStatus { provider: string; enabled: boolean; configured: boolean; state: string }

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
  summary?: { summary: string; callerIntent?: string; outcome: string; followUpRequired: boolean; escalated: boolean; generatedAtUtc: string }
}

export interface CallDetails { call: CallSummary; conversation?: ConversationDetails }

export interface DeadLetterSummary {
  messageId: string
  tenantId: string
  locationId: string
  messageType: string
  status: string
  attemptCount: number
  createdAtUtc: string
  lastAttemptAtUtc?: string
  deadLetteredAtUtc?: string
  correlationId: string
  traceId?: string
  failureCategory: string
  failureSummary: string
  recoveryCount: number
  lastRecoveredAtUtc?: string
}

export interface DeadLetterDetail {
  message: DeadLetterSummary
  causationId?: string
  traceParent?: string
  traceState?: string
  producer: string
  dataClassification: string
  lastRecoveryCorrelationId?: string
  recoverable: boolean
}

export interface DeadLetterPage { items: DeadLetterSummary[]; page: number; pageSize: number; totalCount: number }
export interface DeadLetterFilters { locationId?: string; messageType?: string; failureCategory?: string; page?: number; pageSize?: number }

const rawBaseQuery = fetchBaseQuery({ baseUrl: '/bff/v1', credentials: 'same-origin' })
let csrfToken: string | undefined

export function isMutation(args: string | FetchArgs): boolean {
  if (typeof args === 'string') return false
  return ['POST', 'PUT', 'PATCH', 'DELETE'].includes((args.method ?? 'GET').toUpperCase())
}

export function clearCsrfToken(): void { csrfToken = undefined }

const secureBaseQuery: BaseQueryFn<string | FetchArgs, unknown, FetchBaseQueryError> = async (args, api, extraOptions) => {
  let securedArgs = args
  if (isMutation(args)) {
    if (!csrfToken) {
      const tokenResult = await rawBaseQuery('/security/csrf', api, extraOptions)
      if (tokenResult.error) return tokenResult
      csrfToken = (tokenResult.data as { token: string }).token
    }
    const headers = new Headers(typeof args === 'string' ? undefined : args.headers as HeadersInit)
    headers.set('X-CSRF-TOKEN', csrfToken)
    securedArgs = typeof args === 'string' ? args : { ...args, headers }
  }
  const result = await rawBaseQuery(securedArgs, api, extraOptions)
  if (result.error?.status === 401) clearCsrfToken()
  if (result.error && result.error.status !== 401 && result.error.status !== 403) {
    const response = result.meta?.response
    reportOperationalFailure('api.request_failed', response?.headers.get('X-Correlation-Id') ?? undefined)
  }
  return result
}

export const prototypeApi = createApi({
  reducerPath: 'prototypeApi',
  baseQuery: secureBaseQuery,
  tagTypes: ['Session', 'TenantSummary', 'Calls', 'DeadLetters'],
  endpoints: (builder) => ({
    getSession: builder.query<SessionProjection, void>({ query: () => '/session', providesTags: ['Session'] }),
    developmentLogin: builder.mutation<void, 'administrator' | 'read-only'>({
      query: (user) => ({ url: '/security/development-login', method: 'POST', body: { user } }),
      invalidatesTags: ['Session'],
    }),
    logout: builder.mutation<void, void>({ query: () => ({ url: '/logout', method: 'POST' }) }),
    getTenantSummary: builder.query<TenantSummary, void>({ query: () => '/tenant-summary', providesTags: ['TenantSummary'] }),
    updateLocationName: builder.mutation<TenantSummary, { locationId: string; displayName: string; expectedVersion: number }>({
      query: ({ locationId, ...body }) => ({ url: `/locations/${locationId}/display-name`, method: 'PUT', body }),
      invalidatesTags: ['TenantSummary'],
    }),
    updateLocationDefaultCallLanguage: builder.mutation<TenantSummary, { locationId: string; languageCode: string; expectedVersion: number }>({
      query: ({ locationId, ...body }) => ({ url: `/locations/${locationId}/default-call-language`, method: 'PUT', body }),
      invalidatesTags: ['TenantSummary'],
    }),
    getCalls: builder.query<CallSummary[], void>({ query: () => '/calls?limit=20', providesTags: ['Calls'] }),
    getCallDetails: builder.query<CallDetails, string>({ query: (callId) => `/calls/${callId}`, providesTags: ['Calls'] }),
    getTelephonyStatus: builder.query<TelephonyStatus, void>({ query: () => '/telephony/status' }),
    startOutboundCall: builder.mutation<CallSummary, { locationId: string; destinationNumber: string; idempotencyKey: string; languageCode?: string }>({
      query: (body) => ({ url: '/calls/outbound', method: 'POST', body }),
      invalidatesTags: ['Calls'],
    }),
    hangupCall: builder.mutation<CallSummary, string>({
      query: (callId) => ({ url: `/calls/${callId}/hangup`, method: 'POST' }),
      invalidatesTags: ['Calls'],
    }),
    getDeadLetters: builder.query<DeadLetterPage, DeadLetterFilters>({
      query: (filters) => ({ url: '/operations/dead-letters', params: filters }),
      providesTags: ['DeadLetters'],
    }),
    getDeadLetter: builder.query<DeadLetterDetail, string>({
      query: (messageId) => `/operations/dead-letters/${messageId}`,
      providesTags: ['DeadLetters'],
    }),
    retryDeadLetter: builder.mutation<{ result: string }, string>({
      query: (messageId) => ({ url: `/operations/dead-letters/${messageId}/retry`, method: 'POST' }),
      invalidatesTags: ['DeadLetters'],
    }),
  }),
})

export const {
  useGetSessionQuery, useDevelopmentLoginMutation, useLogoutMutation,
  useGetTenantSummaryQuery, useUpdateLocationNameMutation, useGetCallsQuery, useGetCallDetailsQuery,
  useUpdateLocationDefaultCallLanguageMutation,
  useGetTelephonyStatusQuery, useStartOutboundCallMutation, useHangupCallMutation,
  useGetDeadLettersQuery, useGetDeadLetterQuery, useRetryDeadLetterMutation,
} = prototypeApi
