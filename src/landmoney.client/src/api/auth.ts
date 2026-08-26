import { ApiError, request } from './http'

/** Who is signed in, as the server sees it. */
export interface Me {
  /** The id rows are owned by. Not shown anywhere; kept because it is the answer. */
  ownerId: string | null
  /** The username, which is also the display name -- no email is collected. */
  name: string | null
}

/** The signed-in user, or null when nobody is. */
// The one request in this client that treats a 401 as an answer rather than as a
// failure: "nobody is signed in" is exactly what it was asking, and throwing here
// would make the caller catch an error to learn a fact.
export async function getMe(signal?: AbortSignal): Promise<Me | null> {
  try {
    return await request<Me>('/api/me', { method: 'GET' }, signal)
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      return null
    }

    throw error
  }
}

/** Signs in and returns who was signed in. Rejects with an {@link ApiError}. */
export function login(userName: string, password: string): Promise<Me> {
  return post<Me>('/api/auth/login', { userName, password })
}

/** Creates an account, signs into it, and returns it. */
// The invite code is optional here because it is optional on the wire: in
// Development with none configured the server asks for none. The form still shows
// the field, because a client cannot know which of those two a server is -- and a
// field that is sometimes ignored is better than one that is sometimes missing.
export function register(
  userName: string,
  password: string,
  inviteCode: string,
): Promise<Me> {
  return post<Me>('/api/auth/register', { userName, password, inviteCode })
}

/** Ends the session. */
// Deliberately not returning anything, and deliberately not throwing on failure
// being handled by the caller: the only sensible response to "sign out did not
// work" is to reload, which is what the caller does anyway.
export async function logout(): Promise<void> {
  await post<void>('/api/auth/logout', {})
}

// POST with a JSON body, which is the only shape these three take. Written once
// here rather than three times, and it carries the Content-Type header for the
// same reason createTransaction does: minimal APIs bind a JSON body only when the
// request says it is sending one, and `fetch` does not infer it from a string.
//
// That header is also load-bearing for something less obvious. A form on another
// site can POST to this origin, but it cannot set Content-Type to application/json
// without a CORS preflight this server never answers -- so these endpoints cannot
// be driven from a page the user did not open. The cookie's SameSite=Lax is the
// real protection; this is the second lock on the same door, and it is the reason
// there is no antiforgery token anywhere in this application.
function post<T>(url: string, body: unknown): Promise<T> {
  return request<T>(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
}
