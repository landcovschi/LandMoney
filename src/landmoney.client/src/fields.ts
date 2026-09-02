import type { FieldErrors } from './api/types'

/** What the two entry forms share that is not a component. #94. */
// A module of its own rather than three more exports from
// components/TransactionFields.tsx, for the reason #68 moved `summariseMonth`
// out of MonthSummary: a file that exports both a component and a constant
// breaks Vite's fast refresh, and oxlint says so. It is the smaller of the two
// reasons in both cases -- the larger one is that these are testable values with
// no JSX in them, and a file with no JSX is one dependency away from having a
// test rather than a refactor away.

/** The three codes the entity's own documentation names. */
// A <select> rather than a text input because the server validates the *shape* of
// a currency and not the code: "^[A-Za-z]{3}$" catches "EU" and accepts "XYZ"
// without a murmur. A list refuses the typo before it can be made.
//
// What that gives up is every other currency -- spending in RON or GBP cannot be
// entered until this array grows. The right trade at one user with three
// currencies, and the wrong one the day it is not; the fix then is a list served
// by the API, not a longer array here.
//
// It is not the closed set the eleven categories are, and #94 is where that stops
// being theoretical: the CSV import validates a currency's shape and not its
// membership, so a file carrying RON stores RON, and the edit form has to be able
// to show a row it cannot offer.
export const CURRENCIES = ['EUR', 'MDL', 'USD']

/** The fields an entry form has somewhere to put a message. */
const OWN_FIELDS = new Set(['occurredAt', 'amount', 'currency', 'description'])

/** The messages the server filed under a key the form has no input for. */
// The empty key it uses for a rule about the object as a whole, or a field added
// to the API and not yet added here. Shown at the top of whichever form is asking
// rather than dropped -- a 400 that produces no visible message is
// indistinguishable from a button that did nothing.
export function unattachedMessages(fieldErrors: FieldErrors): string[] {
  return Object.entries(fieldErrors)
    .filter(([field]) => !OWN_FIELDS.has(field))
    .flatMap(([, messages]) => messages ?? [])
}
