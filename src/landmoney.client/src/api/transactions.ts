import type { FieldErrors, NewTransaction, Transaction } from './types'

/** How long a request may take before it is given up on. */
// The repository's rule that every network client gets a timeout, applied on
// this side of the wire as well. `fetch` has none of its own: without this, an
// API that accepts the connection and then stops answering leaves the button
// spinning for as long as the user is willing to watch. That is the browser's
// version of the hang an HttpClient without a Timeout produces, and it costs
// just as much to diagnose.
//
// Ten seconds is generous for a Postgres on the same machine, deliberately.
// This is a backstop against a hang, not a latency budget.
const REQUEST_TIMEOUT_MS = 10_000

// One path for both verbs, written once, and no trailing slash. The route the
// server registers does have one -- MapGroup("/api/transactions") combined with
// MapPost("/") produces the pattern "/api/transactions/", which is visible in
// the endpoint name ASP.NET reports. It matches either way, because routing
// treats a trailing slash as optional, which is the only reason this and
// LandMoney.Web.http can both post to the bare path.
const TRANSACTIONS_URL = '/api/transactions'

/**
 * A request that failed in a way there is something to tell the user about.
 */
// Every reason a request can fail arrives as this one type, so a caller has one
// thing to catch and one place to read a message from. The exception is a
// caller-requested abort, which is rethrown untouched below -- it is not a
// failure and there is no one left to tell.
export class ApiError extends Error {
  /** What the server filed against a named field. Empty for anything else. */
  readonly fieldErrors: FieldErrors

  constructor(message: string, fieldErrors: FieldErrors = {}) {
    super(message)

    // Set by hand because `Error` does not do it and the class name does not
    // survive minification. The C# parallel is that an exception type is its
    // own name; here the name has to be written down separately.
    this.name = 'ApiError'
    this.fieldErrors = fieldErrors
  }
}

/** Every transaction, newest first. The server decides the order. */
export function listTransactions(signal?: AbortSignal): Promise<Transaction[]> {
  return request<Transaction[]>(TRANSACTIONS_URL, { method: 'GET' }, signal)
}

/** Creates one transaction and returns the row the server stored. */
export function createTransaction(
  transaction: NewTransaction,
  signal?: AbortSignal,
): Promise<Transaction> {
  return request<Transaction>(
    TRANSACTIONS_URL,
    {
      method: 'POST',

      // Without this header the endpoint answers 415: minimal APIs bind a JSON
      // body only when the request says it is sending one. `fetch` does not
      // infer it from a string body.
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(transaction),
    },
    signal,
  )
}

async function request<T>(
  url: string,
  init: RequestInit,
  callerSignal?: AbortSignal,
): Promise<T> {
  // Two independent reasons to give up, and both have to reach the same fetch:
  // the timeout above, and the caller changing its mind -- a component
  // unmounting, or StrictMode running an effect a second time. AbortSignal.any
  // is the DOM's linked cancellation source; the C# parallel is
  // CancellationTokenSource.CreateLinkedTokenSource, down to the fact that the
  // reason arriving afterwards says which of the two fired.
  const timeoutSignal = AbortSignal.timeout(REQUEST_TIMEOUT_MS)
  const signal = callerSignal
    ? AbortSignal.any([timeoutSignal, callerSignal])
    : timeoutSignal

  let response: Response

  try {
    response = await fetch(url, { ...init, signal })
  } catch (error) {
    // The caller aborted. Not a failure and not this function's to describe:
    // whatever asked for the request has already stopped caring. Rethrown as it
    // is, so the caller can recognise its own abort and stay quiet about it.
    if (callerSignal?.aborted) {
      throw error
    }

    // AbortSignal.timeout aborts with a DOMException named TimeoutError, which
    // is the only thing distinguishing "we gave up waiting" from "the network
    // refused". Both arrive here as the same rejected fetch.
    if (error instanceof DOMException && error.name === 'TimeoutError') {
      throw new ApiError(
        `The API did not answer within ${REQUEST_TIMEOUT_MS / 1000} seconds.`,
      )
    }

    // Everything else `fetch` rejects with is a TypeError carrying a message
    // the browser chose -- "Failed to fetch" in Chrome, "NetworkError when
    // attempting to fetch resource" in Firefox. That message is deliberately
    // not shown: it names neither the cause nor the fix.
    throw unreachable()
  }

  // A gateway status is the same fact arriving as a response instead of as a
  // rejection, and it is the *usual* way this failure shows up here. Nothing
  // in development talks to the API directly: the dev proxy does, so a refused
  // connection never reaches the browser as a failed fetch -- Vite catches the
  // ECONNREFUSED and answers 502 itself. Without this branch the commonest
  // mistake in the whole loop, forgetting to start the API, reads as "The API
  // answered 502.", which blames something that answered nothing.
  //
  // Correct beyond the dev proxy, too: 502 and 504 both mean an intermediary
  // could not get an answer out of the origin, whoever the intermediary is.
  // 503 is deliberately not in the list -- that one the origin sends about
  // itself, and it has something of its own to say.
  if (response.status === 502 || response.status === 504) {
    throw unreachable()
  }

  if (!response.ok) {
    throw await toApiError(response)
  }

  // `as T` is an assertion, not a check: at runtime this is whatever the server
  // sent, and nothing here proves it matches Transaction. This is the seam
  // where TypeScript's guarantees actually stop, and it is worth knowing where
  // that is rather than trusting the annotation everywhere equally.
  //
  // A schema validator (zod and its like) closes the seam by checking the shape
  // at runtime, and is the usual answer. It lost here on the dependency rule
  // and on proportion: one producer, one consumer, seven fields, both in this
  // repository. It stops being disproportionate the moment the API is not.
  return (await response.json()) as T
}

/** The API is not answering, however that turned out to be discovered. */
// Two call sites reach this -- a rejected fetch and a gateway status -- and
// they are the same fact to whoever is reading the screen, so they get the same
// sentence.
//
// import.meta.env.DEV is replaced by a literal at build time and the dead
// branch is then dropped, so the hint cannot survive into the production
// bundle, where "localhost:5150" would be nonsense: the built files are served
// by the .NET app itself, on the same origin as the API, with no proxy in
// between and nothing to start separately.
function unreachable(): ApiError {
  return new ApiError(
    'Could not reach the API.' +
      (import.meta.env.DEV
        ? ' It has to be running on http://localhost:5150, started with --launch-profile http.'
        : ''),
  )
}

/** The parts of an RFC 9457 problem document this client reads. */
// Every field optional, because several things produce a non-2xx here and they
// fill in different parts. ValidationFilter<T> returns `errors` keyed by field.
// A failure in the model binder -- a missing `required` member, malformed JSON
// -- returns `detail` and no `errors`, which is why `toApiError` falls back
// through detail and then title rather than assuming the dictionary is there.
//
// That second case used to be a bare 400 with no body at all. Program.cs calls
// AddProblemDetails() as of the day the Razor leftovers went, because
// UseExceptionHandler needs it to have anything to write, and giving the binder
// a body came with it. Still keep the null path below: a proxy or the host can
// answer with something that is not a problem document at all, and 502 from the
// dev proxy is exactly that.
interface Problem {
  title?: string
  detail?: string
  errors?: Record<string, string[]>
}

async function toApiError(response: Response): Promise<ApiError> {
  const problem = await readProblem(response)

  if (problem?.errors && Object.keys(problem.errors).length > 0) {
    // The title is "One or more validation errors occurred." and says nothing
    // the field messages do not. It is carried anyway, so that a caller with
    // nowhere to put field messages still has a sentence to show.
    return new ApiError(
      problem.title ?? 'The API rejected the transaction.',
      problem.errors,
    )
  }

  // response.statusText is deliberately not used: HTTP/2 dropped the reason
  // phrase, so it is an empty string on any connection negotiated as h2, and a
  // message reading "The API answered 500 ." is worse than one without it.
  return new ApiError(
    problem?.detail ?? problem?.title ?? `The API answered ${response.status}.`,
  )
}

async function readProblem(response: Response): Promise<Problem | null> {
  // Nothing in here may throw. It runs while an error is already being built,
  // and a parse failure inside it would replace a 400 that could have been
  // reported with one that cannot -- the status would be lost behind a
  // SyntaxError about a body nobody asked about.
  if (!response.headers.get('content-type')?.includes('json')) {
    return null
  }

  try {
    return (await response.json()) as Problem
  } catch {
    return null
  }
}
