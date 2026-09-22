import { describe, expect, it } from 'vitest'

import { ApiError, ConflictError, ValidationError, toApiError } from '@/utils/api'

describe('toApiError', () => {
  it('lifts problem+json with field errors into a ValidationError', () => {
    const error = toApiError({
      status: 400,
      data: {
        status: 400,
        title: 'One or more validation errors occurred.',
        errors: { title: ['Title is required.'] },
      },
    })

    expect(error).toBeInstanceOf(ValidationError)
    expect(error.status).toBe(400)
    expect(error.title).toBe('One or more validation errors occurred.')
    expect(error.fieldErrors).toEqual({ title: ['Title is required.'] })
  })

  it('leaves a 400 without field errors as a plain ApiError', () => {
    const error = toApiError({ status: 400, data: { status: 400, title: 'Bad request.' } })

    expect(error).toBeInstanceOf(ApiError)
    expect(error).not.toBeInstanceOf(ValidationError)
  })

  it('maps 409 to ConflictError - someone else saved first', () => {
    const error = toApiError({ status: 409, data: { status: 409, title: 'Conflict.' } })

    expect(error).toBeInstanceOf(ConflictError)
    expect(error.status).toBe(409)
  })

  it('falls back to the transport status when the body is not problem+json', () => {
    const error = toApiError({ status: 502 })

    expect(error.status).toBe(502)
    expect(error.title).toBe('Request failed (502)')
    expect(error.fieldErrors).toEqual({})
  })

  it('reports a network failure as status 0', () => {
    const error = toApiError(new TypeError('Failed to fetch'))

    expect(error.status).toBe(0)
    expect(error.title).toBe('Network error')
  })

  it('flags an unauthenticated response for the route guards', () => {
    expect(new ApiError(401, 'Unauthorized').isUnauthenticated).toBe(true)
    expect(new ApiError(404, 'Not Found').isUnauthenticated).toBe(false)
  })

  it('passes an ApiError through unchanged', () => {
    const original = new ApiError(403, 'Forbidden')
    expect(toApiError(original)).toBe(original)
  })
})
