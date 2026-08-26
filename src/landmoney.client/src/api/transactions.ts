import { request } from './http'
import type { NewTransaction, Transaction } from './types'

// ApiError is re-exported rather than moved out of sight: every component that
// catches one imports it from here, and #52 moving the class to http.ts is not a
// reason to touch six other files.
export { ApiError } from './http'

// One path for both verbs, written once, and no trailing slash. The route the
// server registers does have one -- MapGroup("/api/transactions") combined with
// MapPost("/") produces the pattern "/api/transactions/", which is visible in
// the endpoint name ASP.NET reports. It matches either way, because routing
// treats a trailing slash as optional, which is the only reason this and
// LandMoney.Web.http can both post to the bare path.
const TRANSACTIONS_URL = '/api/transactions'

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

