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
