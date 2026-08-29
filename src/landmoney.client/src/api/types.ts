// The TypeScript half of the contract defined in
// src/LandMoney.Web/Api/TransactionContracts.cs. Written out by hand and kept in
// step by hand, which is the part worth being honest about: nothing checks that
// these two files still agree. The compiler catches a field this file gets
// wrong everywhere it is used, and says nothing at all about a field the API
// renamed underneath it.
//
// Generating this from an OpenAPI document is the answer that removes the
// drift, and it lost for now on cost: OpenAPI has to be turned on, a generator
// added as a dependency, and a step added to the build, for a contract of seven
// fields that one person maintains. The moment to revisit is when the Python
// categorizer in slice 4 becomes a third party to the same shapes.

/** One transaction, exactly as `GET /api/transactions` returns it. */
export interface Transaction {
  /** C# `Guid`. A string here -- JavaScript has no GUID type. */
  id: string

  /**
   * C# `DateOnly`, on the wire as "2026-08-19": a plain day, no time, no zone.
   *
   * A string on purpose, and it stays a string all the way to the screen.
   * `new Date("2026-08-19")` parses as *UTC* midnight, so everyone west of UTC
   * renders the day before -- the same day-boundary bug #17 removed from
   * storage, arriving again on the client. There is nothing here to convert:
   * the value already reads the way a human writes a date.
   */
  occurredAt: string

  /**
   * C# `decimal`, on the wire as a JSON number, decided in #3.
   *
   * `JSON.parse` makes it an IEEE 754 double, and a double round-trips any
   * decimal of at most 15 significant digits exactly. These are `numeric(18,2)`
   * amounts, so the guarantee holds with room to spare -- as long as nothing
   * here does arithmetic with it. Format it; never add it to another currency.
   */
  amount: number

  /** ISO 4217, three letters, upper-cased by the server. Never converted. */
  currency: string

  description: string

  /**
   * `null` until the categorizer in slice 4 fills it.
   *
   * `string | null` rather than `string | undefined`, because `null` is what
   * JSON actually carries and what `JSON.parse` produces. Declaring the
   * friendlier of the two would make the type disagree with the bytes.
   */
  category: string | null

  /**
   * What produced the category: `rules`, `model`, `human`, or null with it.
   *
   * Added in #63, and it is the field that makes a correction visible. Without
   * it a category a person chose and a category a substring match guessed are
   * the same word on the screen, and after a week nothing can tell them apart.
   *
   * A plain string rather than a union of the three literals, for the same
   * reason `ImportRowProblem.outcome` is one: two of the three values are
   * another process's words arriving over HTTP, so a union here would be a
   * compile-time promise about what the categorizer sends. The constants below
   * are compared against; an unrecognised source is shown as it arrived rather
   * than hidden.
   *
   * The invariant, established in #59 and checked against the running database:
   * this is non-null exactly when `category` is. Both, or neither.
   */
  categorySource: string | null

  /**
   * C# `DateTimeOffset`, ISO 8601 with an offset.
   *
   * An instant, unlike `occurredAt` -- so this one *is* converted to local time
   * for display, and that is correct rather than a slip. The two fields are the
   * two halves of the rule: display converts, storage does not.
   */
  createdAt: string
}

/** What `POST /api/transactions` accepts: `CreateTransactionRequest`. */
// Four fields, not seven. `id`, `createdAt` and `category` are the server's to
// decide, and a request type that does not offer them is what stops a client
// overwriting them -- the reasoning is on the C# record.
export interface NewTransaction {
  occurredAt: string
  amount: number
  currency: string
  description: string
}

/** The three things that can have decided a category. Mirrors `CategorySources`. */
// Only `human` is this application's own word -- the other two are the
// categorizer's, arriving over HTTP. They are here so the badge can be styled and
// labelled per source without three string literals appearing in JSX, and not so
// that an unknown fourth value is hidden: CategoryCell prints whatever it is given.
export const SOURCE_RULES = 'rules'
export const SOURCE_MODEL = 'model'
export const SOURCE_HUMAN = 'human'

/** What `PATCH /api/transactions/{id}` accepts: `UpdateCategoryRequest`. */
// One field, and the omission is the point rather than an economy. #63: do not
// send the whole transaction back to save one field, because a PATCH that accepts
// an amount is a way to overwrite money with a stale value from a screen somebody
// left open. There is nothing to keep in step here -- a field this type does not
// have cannot be sent by mistake.
//
// `category: string | null` and never `undefined`. The C# record declares the
// member `required`, so System.Text.Json refuses a body that omits it; `undefined`
// would be dropped by JSON.stringify and produce exactly that 400. Clearing a
// category is `null`, spelled out.
export interface CategoryUpdate {
  category: string | null
}

/** The `errors` object of an RFC 9457 problem: field name to what is wrong. */
// The keys are camelCase because ValidationFilter<T> runs the member names
// through JsonNamingPolicy.CamelCase before it builds the dictionary. That is
// the entire reason the filter bothers: "occurredAt" matches the `name` of the
// input that produced it, so the message can be shown beside its own field
// instead of in an anonymous list at the top of the form.
//
// `| undefined` is spelled out because `noUncheckedIndexedAccess` is off: without
// it, `errors.amount` is typed `readonly string[]` and is `undefined` at runtime
// whenever the amount was fine, which is most of the time.
export type FieldErrors = Readonly<Record<string, readonly string[] | undefined>>

/** What one row of an uploaded CSV did, when it did not simply import. */
// `outcome` is a plain string rather than a union of two literals because the
// server sends whatever ImportOutcomes holds, and a union here would be a
// compile-time promise about another process's data. The two values are compared
// against the constants below, and an unrecognised one falls through to being
// shown as-is rather than being hidden.
export interface ImportRowProblem {
  /** The line of the file, 1-based, header included. */
  lineNumber: number
  outcome: string
  reason: string
}

/** The two outcomes the server sends today. Mirrors `ImportOutcomes` in C#. */
export const IMPORT_SKIPPED = 'skipped'
export const IMPORT_REJECTED = 'rejected'

/** What `POST /api/transactions/import` answers: `ImportResponse`. */
// rows === imported + skipped + rejected, always, and the counts are exact even
// when `problems` has been truncated -- so a screen built from the counts is
// never the thing that is missing.
export interface ImportResult {
  rows: number
  imported: number
  skipped: number
  rejected: number

  /** Header columns that were read and ignored, such as `category`. */
  ignoredColumns: readonly string[]

  /** True when `problems` holds fewer entries than skipped + rejected. */
  problemsTruncated: boolean

  problems: readonly ImportRowProblem[]
}

/** What `POST /api/transactions/category-suggestion` accepts. #67. */
// Three fields and no date, mirroring `CategorySuggestionRequest` in C#. The day
// money was spent tells a predictor nothing, and a field the endpoint does not
// read is a field this form could be refused for getting wrong -- a mistyped year
// would otherwise stop the suggestion appearing for a reason that has nothing to
// do with the description.
//
// The amount is a number here, where the form holds it as a string. That is the
// same conversion `NewTransaction` needs and it happens in the same place, once,
// at the edge -- see useCategorySuggestion, which refuses to ask at all until the
// text is something the server would accept.
export interface CategorySuggestionQuery {
  amount: number
  currency: string
  description: string
}

/** What it answers: who answered, and what they said. */
// **The source is what says something answered**, and reading it any other way
// loses the distinction the endpoint exists to carry:
//
//   { category: 'groceries', source: 'rules' }  a suggestion
//   { category: null, source: 'rules' }         it answered, and had no idea
//   { category: null, source: null }            nothing answered
//
// The middle one is a normal answer on roughly a third of the labelled set, so a
// screen that renders nothing for it looks broken every third transaction. The
// last one is a categorizer that is not running, and it has to be invisible --
// there is nothing the person typing could do about it, and #67 asks for the field
// to show nothing extra in exactly that case.
export interface CategorySuggestion {
  category: string | null
  source: string | null
}
