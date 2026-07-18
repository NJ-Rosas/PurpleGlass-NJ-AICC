import { createApi, fetchBaseQuery } from '@reduxjs/toolkit/query/react'

export interface TenantSummary {
  tenantId: string
  tenantDisplayName: string
  locationId: string
  locationDisplayName: string
  timeZoneId: string
  version: number
}

export const prototypeApi = createApi({
  reducerPath: 'prototypeApi',
  baseQuery: fetchBaseQuery({ baseUrl: '/bff/v1' }),
  tagTypes: ['TenantSummary'],
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
  }),
})

export const { useGetTenantSummaryQuery, useUpdateLocationNameMutation } = prototypeApi
