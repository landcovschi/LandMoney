import { request, send } from './http'
import type {
  BackfillCount,
  BackfillResult,
  CategorySuggestion,
  CategorySuggestionQuery,
  CategoryUpdate,
  ImportResult,
  MonthSummary,
  NewTransaction,
  Transaction,
  TransactionPage,
  UpdateTransaction,
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

// #89. A path of its own under the transactions group rather than a query
// parameter on the list, because it is a different representation of a different
// subset -- and because a `?format=csv` on the list is how a screen ends up
// fetching a CSV by accident.
const LABELLED_URL = '/api/transactions/labelled'

// Written here and in TransactionEndpoints.cs, which is the two-places problem
// this repository names rather than pretends away. It is a header name and not a
// rule: getting it wrong costs a row count that reads 0, which the screen says out
// loud, and never a wrong file.
const ROW_COUNT_HEADER = 'X-Labelled-Rows'

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

/** How far into the list to ask, and where from. #95. */
export interface PageQuery {
  /** How many rows. The server clamps it; omitting it takes the server's default. */
  limit?: number

  /** The `nextCursor` of the previous page, or nothing for the newest rows. */
  // Opaque, and passed back exactly as it arrived. Nothing here builds one or reads
  // one apart -- TransactionCursor.cs is the only thing that knows the shape, which
  // is what let the server add a third sort key to it without touching this file.
  cursor?: string
}

/** One page of transactions, newest first, with the token for the next. #95. */
// **The endpoint used to answer every row and now answers fifty**, and this is the
// function where that has to be dealt with rather than absorbed. A wrapper returning
// `page.items` would compile everywhere the old one did and would leave every caller
// quietly describing a page as the table -- which is #68's "it stops being fine
// silently", arriving through the fix for it.
export function listTransactions(
  query: PageQuery = {},
  signal?: AbortSignal,
): Promise<TransactionPage> {
  // URLSearchParams rather than string concatenation, and it earns itself on the
  // cursor: base64url avoids "+" and "/" but the class of bug is one careless
  // encoding away, and a mangled cursor is a 400 that reads like the list is broken.
  const params = new URLSearchParams()

  if (query.limit !== undefined) {
    params.set('limit', String(query.limit))
  }

  if (query.cursor !== undefined) {
    params.set('cursor', query.cursor)
  }

  const search = params.toString()

  return request<TransactionPage>(
    search ? `${TRANSACTIONS_URL}?${search}` : TRANSACTIONS_URL,
    { method: 'GET' },
    signal,
  )
}

/** What one month cost, by currency and then by category. #95. */
// A request rather than a pass over the rows on screen, which is the whole of #95's
// third trap: a paged list cannot be summed by whoever happens to be holding part of
// it. The month is this client's to decide -- see `monthOf` -- because `occurredAt`
// is a plain day with no zone, so which month a row falls in is a question only the
// reader's calendar answers.
export function monthSummary(
  month: string,
  signal?: AbortSignal,
): Promise<MonthSummary> {
  return request<MonthSummary>(
    `${TRANSACTIONS_URL}/summary?month=${encodeURIComponent(month)}`,
    { method: 'GET' },
    signal,
  )
}

/** How many transactions a backfill would queue, and therefore pay for. #95. */
// A GET on the path `backfillCategories` posts to, which is the server's argument
// rather than this file's: the two are one collection asked about the two ways HTTP
// has. It is here because #93's count was arithmetic over the loaded rows and a
// loaded page is no longer the table -- and getting that wrong is not a wrong number
// on a screen, it is a bill.
export function countBackfillable(signal?: AbortSignal): Promise<BackfillCount> {
  return request<BackfillCount>(
    `${TRANSACTIONS_URL}/backfill-categories`,
    { method: 'GET' },
    signal,
  )
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

/** Replaces the four fields a person typed, and returns the row the server stored. #94. */
// PUT with the same body a create takes, which is the client half of the C#
// decision: `UpdateTransactionRequest` derives from `CreateTransactionRequest`, so
// there is exactly one set of rules and this sends exactly the same four fields.
// The type alias below says so; a second interface would be the drift that
// inheritance exists to prevent, written in the other language.
//
// The returned row is the point, the same way it is for `updateCategory`: it lets
// the caller put the corrected row on screen without dropping the whole table to
// "Loading...". It may come back with no category at all -- an edit to the
// description, the amount or the currency clears the old prediction and re-queues
// the row -- which is why the response is used rather than the values that were
// sent.
export function updateTransaction(
  id: string,
  transaction: UpdateTransaction,
  signal?: AbortSignal,
): Promise<Transaction> {
  return request<Transaction>(
    `${TRANSACTIONS_URL}/${encodeURIComponent(id)}`,
    {
      method: 'PUT',

      // The same header the POST and the PATCH need, and for the same two
      // reasons: minimal APIs bind a JSON body only when the request says it is
      // sending one, and a content type no cross-site form can produce is one of
      // the two CSRF locks AuthenticationSetup.cs records.
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(transaction),
    },
    signal,
  )
}

/** Removes one transaction. #94. */
// Answers 204, so there is nothing to read: `request` returns undefined for a
// body-less success, which is the branch #52 added for `/api/auth/logout`.
//
// A 404 arrives as an ApiError like any other refusal, and it is the answer for
// both "there is no such row" and "it is not yours" -- the server cannot tell the
// caller which without confirming that somebody else's id exists. A second delete
// of the same row gets it too, which is the honest reading rather than a wart: the
// row is gone either way.
export function deleteTransaction(
  id: string,
  signal?: AbortSignal,
): Promise<void> {
  return request<void>(
    `${TRANSACTIONS_URL}/${encodeURIComponent(id)}`,
    { method: 'DELETE' },
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

/** How long an export may take before the client gives up on it. */
// Between the ten seconds every other call gets and the import's two minutes, and
// chosen against the same shape of work: the server runs one indexed query,
// renders a few hundred rows into a string and sends it -- but on the deployed app
// this may also be the request that pays the 23 second cold start (#35). Thirty
// seconds is a backstop, not a budget.
const EXPORT_TIMEOUT_MS = 30_000

/** What the server sent, and what to call the file it goes into. */
// Not in api/types.ts, deliberately: everything there is a shape the server
// serialises, and this is one this module assembles out of a body and two headers.
// Putting it there would suggest there is a JSON contract behind it, and the whole
// point of the endpoint is that there is not -- the body is the file.
export interface LabelledExport {
  csv: string
  rows: number
  fileName: string
}

/** The name the server suggested, or a plain one if it did not. #89. */
// A deliberately narrow parse of one header this application writes itself, rather
// than a general Content-Disposition reader -- the RFC's grammar has parameter
// ordering, RFC 5987 encoding and unquoted forms in it, none of which this server
// produces. The fallback matters more than the parse: a browser extension or a
// proxy that strips the header must cost a nice filename and nothing else.
function fileNameFrom(disposition: string | null): string {
  return /filename="([^"]+)"/.exec(disposition ?? '')?.[1] ?? 'labelled.csv'
}

/** Every row a person has labelled by hand, as the five columns the eval set holds. #89. */
// Read as text rather than JSON, which is why this is the one function in this file
// that reaches for `send` instead of `request`. The body is a file: wrapping it in
// an envelope would put the whole export through JSON escaping and would make
// anything but this client -- curl, a script -- unwrap it before it had a CSV.
//
// The row count comes from a header for the same reason, and because counting lines
// here would be wrong on exactly the rows worth having: a quoted description may
// contain a newline, so lines and rows are not the same number.
export async function exportLabelled(
  signal?: AbortSignal,
): Promise<LabelledExport> {
  const response = await send(
    LABELLED_URL,
    { method: 'GET' },
    signal,
    { timeoutMs: EXPORT_TIMEOUT_MS },
  )

  // Number() rather than parseInt: a header that is not a number at all should be
  // 0 rows and not the leading digits of something else. NaN is guarded below
  // because Number('') is 0 and Number(null) is 0, but Number('two') is NaN.
  const rows = Number(response.headers.get(ROW_COUNT_HEADER))

  return {
    csv: await response.text(),
    rows: Number.isFinite(rows) ? rows : 0,
    fileName: fileNameFrom(response.headers.get('content-disposition')),
  }
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


/** Puts every uncategorised row into the sweep's queue, and says how many. #93. */
// A POST with no body at all, which is unusual enough here to be worth a line: every
// other write in this file sends JSON, and the two CSRF locks
// AuthenticationSetup.cs records are the SameSite=Lax cookie and a content type a
// cross-site form cannot produce. Only the first of those applies to a bodyless
// request -- there is nothing to type -- and it is the one that does the work: a Lax
// cookie is withheld from every cross-site request that is not a top-level GET
// navigation, so a form on another site cannot reach this.
//
// It is the one call in this client that spends money. Every row it marks is a model
// call the sweep will make, at about 0.62 US cents each -- which is why the screen
// shows the count first and this function takes no arguments: there is nothing to
// get wrong between what was shown and what is marked.
export function backfillCategories(signal?: AbortSignal): Promise<BackfillResult> {
  return request<BackfillResult>(
    `${TRANSACTIONS_URL}/backfill-categories`,
    { method: 'POST' },
    signal,
  )
}
