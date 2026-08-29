import { request } from './http'
import type {
  CategorySuggestion,
  CategorySuggestionQuery,
  CategoryUpdate,
  ImportResult,
  NewTransaction,
  Transaction,
} from './types'

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

// #63. A group of its own on the server, not /api/transactions/categories: the
// eleven are not a sub-resource of one transaction, and the day a budget or a
// filter needs them they would be reading a list out of another feature's URLs.
const CATEGORIES_URL = '/api/categories'

/** The closed list the correction dropdown is built from. */
// Fetched rather than written down here, and that is #63's "decide how they stay
// in step" answered rather than accepted. There were three copies of the eleven --
// categories.py, the C# array, and a const in this client. This request is what
// removes the third: the dropdown offers exactly what the server will accept, so
// a correction cannot be offered and then refused. The two that are left are
// pinned to each other by CategoriesTests, which reads categories.py.
//
// The cost is one round trip per page load for about 120 bytes, and a screen that
// has to work when it fails -- App.tsx keeps an empty list and CategoryCell falls
// back to showing the category without a way to change it.
export function listCategories(signal?: AbortSignal): Promise<string[]> {
  return request<string[]>(CATEGORIES_URL, { method: 'GET' }, signal)
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

/** Corrects one transaction's category and returns the row the server stored. */
// PATCH with a one-field body, and the returned row is the point: it is what lets
// the caller put the correction on screen without asking for the whole list again.
// #63 names that as a trap -- the list drops to "Loading..." after a write, and a
// correction is a much worse place for that flicker than a create is.
//
// The `encodeURIComponent` is not decoration for a value the server generated: the
// id comes back over HTTP and is a string here, so treating it as one that must be
// escaped costs nothing and is the habit that holds when the identifier is one
// day a description.
export function updateCategory(
  id: string,
  category: string | null,
  signal?: AbortSignal,
): Promise<Transaction> {
  const body: CategoryUpdate = { category }

  return request<Transaction>(
    `${TRANSACTIONS_URL}/${encodeURIComponent(id)}`,
    {
      method: 'PATCH',

      // The same header the POST needs, and for the same two reasons: minimal
      // APIs bind a JSON body only when the request says it is sending one, and
      // a content type no cross-site form can produce is one of the two CSRF
      // locks AuthenticationSetup.cs records.
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    },
    signal,
  )
}

/** How long an import may take before the client gives up on it. */
// Far above the ten seconds every other call gets, and the reason is the shape of
// the work rather than pessimism: the server reads the file, validates every row,
// runs one query over the whole date range and inserts in a single transaction --
// and on the deployed app this may be the request that pays the container's cold
// start as well. The two numbers on the server side bound it: 5,000 rows and a
// megabyte.
//
// It is still a backstop and not a budget. A file that takes this long has hit
// something wrong, and the message will say so rather than spinning for ever.
const IMPORT_TIMEOUT_MS = 120_000

/** Uploads a CSV file and returns what the server made of every row. */
// The File is sent as the raw request body, not wrapped in FormData, and the
// Content-Type is the point rather than a formality. multipart/form-data is a
// content type a cross-site form can produce; text/csv is not, so this request
// keeps both of the CSRF locks the JSON calls above have. The C# side refuses
// anything else with a 415, and the comment on CsvContentType there is the long
// version of why.
//
// `body: file` rather than `body: await file.text()`: a Blob is a valid fetch
// body, so the browser streams it and the file is never held in JavaScript memory
// as a string. The explicit header is required either way -- fetch would otherwise
// send the File's own type, which for a .csv picked from a Windows machine is
// often "application/vnd.ms-excel".
export function importTransactions(
  file: File,
  signal?: AbortSignal,
): Promise<ImportResult> {
  return request<ImportResult>(
    `${TRANSACTIONS_URL}/import`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'text/csv' },
      body: file,
    },
    signal,
    { timeoutMs: IMPORT_TIMEOUT_MS },
  )
}

/** How long a suggestion may take before it stops being worth having. #67. */
// Far below the ten seconds every other call gets, and for the opposite reason to
// the import's two minutes. That constant is a backstop against a hang on a
// request that must not be abandoned; this one is a deadline on an answer whose
// whole value is that it arrives while the description is still on the screen. A
// suggestion that lands after nine seconds is not a slow success, it is a wrong
// answer about whatever is being typed by then -- #67 says so in as many words.
//
// Five seconds rather than something tighter because of what is on the other side:
// the server gives the categorizer two seconds to connect and eight overall (#59),
// and the model answers in about 2.1 s (#60). Tighter than that would abandon calls
// that were about to succeed, and every abandoned one is still billed.
//
// What it costs, and it is a real cost rather than a rounding: the first request
// after an idle spell pays a cold start -- 23.3 s for the app (#35), and the
// categorizer scales to zero too (#61) -- so it will time out and the field will
// show nothing. That is the right failure. The save that follows has its own,
// longer budget, and this is a suggestion nobody asked for.
const SUGGESTION_TIMEOUT_MS = 5_000

/** What the categorizer says about a description, before anything is saved. #67. */
// Writes nothing: this is the one call in this file with no consequence at all if
// it fails, which is why every caller of it swallows the failure rather than
// showing it. The abort signal is the point of the parameter -- three keystrokes
// make three requests and the second can answer after the third, so the caller
// aborts the superseded one and `request` turns that into a rejection it
// recognises as its own.
export function suggestCategory(
  query: CategorySuggestionQuery,
  signal?: AbortSignal,
): Promise<CategorySuggestion> {
  return request<CategorySuggestion>(
    `${TRANSACTIONS_URL}/category-suggestion`,
    {
      method: 'POST',

      // The same header the POST and the PATCH need, and for the same two
      // reasons: minimal APIs bind a JSON body only when the request says it is
      // sending one, and a content type no cross-site form can produce is one of
      // the two CSRF locks AuthenticationSetup.cs records. It matters more here
      // than elsewhere -- this endpoint writes nothing, so a request nobody
      // noticed would leave no trace except a bill.
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(query),
    },
    signal,
    { timeoutMs: SUGGESTION_TIMEOUT_MS },
  )
}
