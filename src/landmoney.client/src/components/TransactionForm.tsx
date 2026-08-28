import { useState, type FormEvent } from 'react'
import { ApiError } from '../api/transactions'
import type { FieldErrors, NewTransaction } from '../api/types'
import { FieldMessages } from './FieldMessages'

// The three codes the entity's own documentation names. A <select> rather than
// a text input because the server validates the *shape* of a currency and not
// the code: "^[A-Za-z]{3}$" catches "EU" and accepts "XYZ" without a murmur. A
// list refuses the typo before it can be made.
//
// What that gives up is every other currency -- spending in RON or GBP cannot
// be entered until this array grows. The right trade at one user with three
// currencies, and the wrong one the day it is not; the fix then is a list
// served by the API, not a longer array here.
const CURRENCIES = ['EUR', 'MDL', 'USD']

// The fields this form has somewhere to put a message. Anything the server
// files under another key is shown at the top instead of being dropped -- see
// where this is used.
const OWN_FIELDS = new Set(['occurredAt', 'amount', 'currency', 'description'])

/** Today, in the timezone the user is actually in, as "2026-08-19". */
// Not `new Date().toISOString().slice(0, 10)`, which is today in *UTC*: at
// 01:00 in UTC+3 that string is yesterday, and the form would open on the wrong
// day for a third of the world. The date being asked for is the one on the wall
// behind the person typing.
//
// The short version is `toLocaleDateString('sv-SE')` -- Swedish formats dates
// as ISO 8601, so Intl produces exactly this string. It lost for being a trick:
// it works because of a fact about Sweden, which is not a thing the next reader
// should have to know.
function today(): string {
  const now = new Date()
  const month = String(now.getMonth() + 1).padStart(2, '0')
  const day = String(now.getDate()).padStart(2, '0')

  return `${now.getFullYear()}-${month}-${day}`
}

interface TransactionFormProps {
  /**
   * Sends the transaction. Rejects with an {@link ApiError} when the server
   * refuses it, which is how the messages below reach their fields.
   */
  onSubmit: (transaction: NewTransaction) => Promise<void>
}

// What this form validates and what it deliberately leaves alone:
//
// `required`, `step` and `maxLength` describe the *shape* of a value -- a
// number with at most two decimal places, a description that exists at all.
// They are written here because the browser enforces them before a request is
// made, which is faster than a round trip and costs nothing to keep.
//
// The *bounds* are not written here: five years back, one day ahead, the
// ceiling of numeric(18,2). Those are policy, they live on
// CreateTransactionRequest, and copying them into TypeScript would make two
// numbers that have to change together and will not. The server refuses them
// and the sentence it sends is shown beside the field -- which is the whole
// reason ValidationFilter camelCases its keys in the first place.
export function TransactionForm({ onSubmit }: TransactionFormProps) {
  // Four useStates rather than one object holding four fields. The object needs
  // a generic update helper before it saves a line, and the helper is where the
  // typing gets interesting for no benefit at this size. #6 asked for boring.
  const [occurredAt, setOccurredAt] = useState(today)
  const [amount, setAmount] = useState('')
  const [currency, setCurrency] = useState(CURRENCIES[0])
  const [description, setDescription] = useState('')

  const [submitting, setSubmitting] = useState(false)
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    // Without this the browser submits the form the 1995 way: it navigates, the
    // page reloads, and every piece of state above is gone. There is no
    // framework magic involved -- <form> has always done this, and React does
    // not stop it.
    event.preventDefault()

    setSubmitting(true)
    setFieldErrors({})
    setFormError(null)

    try {
      await onSubmit({
        occurredAt,

        // The amount is held as text and becomes a number exactly once, here.
        // `Number` and not `parseFloat`: parseFloat reads as far as it
        // understands and silently ignores the rest, so "12abc" becomes 12,
        // where Number returns NaN. The input's own `required` and `step` have
        // already refused both, which is why this can be the only guard.
        amount: Number(amount),

        currency,
        description,
      })

      // Cleared on success, and only these two. The date and the currency stay
      // put: a week of spending is typed in one sitting, mostly on the same day
      // and in the same currency, and re-picking both every time is the small
      // tax that stops an app being used weekly -- which is the habit slice 4's
      // evals depend on existing.
      setAmount('')
      setDescription('')
    } catch (error) {
      if (error instanceof ApiError) {
        setFieldErrors(error.fieldErrors)

        // The banner and the field messages are deliberately never shown
        // together. "One or more validation errors occurred." above four
        // sentences that each name their own field is noise on top of the
        // answer.
        const hasFieldMessages = Object.keys(error.fieldErrors).length > 0
        setFormError(hasFieldMessages ? null : error.message)
      } else {
        // Not an ApiError means something in this file threw, not the API.
        // Saying so is more honest than blaming the network for it.
        setFormError('Something went wrong while sending the transaction.')
      }
    } finally {
      // In `finally`, so the button comes back whether the request succeeded,
      // was refused or threw. Putting setSubmitting(false) at the end of the
      // `try` leaves the form disabled forever the first time anything fails.
      setSubmitting(false)
    }
  }

  // Messages the server filed under a key this form has no input for: the empty
  // key it uses for a rule about the object as a whole, or a field added to the
  // API and not yet added here. Shown at the top rather than dropped -- a 400
  // that produces no visible message is indistinguishable from a button that
  // did nothing.
  const unattached = Object.entries(fieldErrors)
    .filter(([field]) => !OWN_FIELDS.has(field))
    .flatMap(([, messages]) => messages ?? [])

  const bannerMessages = formError ? [formError] : unattached

  return (
    // noValidate is not set, so the browser's own validation runs first and
    // handleSubmit is never reached with an empty description or a third
    // decimal place. The messages it shows are the browser's, in the browser's
    // language, which is the one part of this screen that is not translated by
    // hand and does not need to be.
    <form className="entry" onSubmit={handleSubmit}>
      <h2>Add a transaction</h2>

      {bannerMessages.length > 0 && (
        // role="alert" is what makes a screen reader announce this the moment
        // it appears. Without it the message is on screen and silent, which for
        // someone not looking at that part of the page is the same as the
        // invisible failure #6 is about.
        <p className="banner banner-error" role="alert">
          {bannerMessages.join(' ')}
        </p>
      )}

      <div className="fields">
        <div className="field field-date">
          <label htmlFor="occurredAt">Date</label>
          <input
            id="occurredAt"
            name="occurredAt"
            type="date"
            required
            value={occurredAt}
            onChange={(event) => setOccurredAt(event.target.value)}
            aria-invalid={fieldErrors.occurredAt ? true : undefined}
            aria-describedby={
              fieldErrors.occurredAt ? 'occurredAt-error' : undefined
            }
          />
          <FieldMessages
            id="occurredAt-error"
            messages={fieldErrors.occurredAt}
          />
        </div>

        <div className="field field-amount">
          <label htmlFor="amount">Amount</label>
          <input
            id="amount"
            name="amount"
            type="number"
            inputMode="decimal"
            // step is client-side scale validation for free, and it is the same
            // rule DecimalScaleAttribute enforces on the server: two decimal
            // places, because numeric(18,2) rounds a third one away in silence.
            step="0.01"
            min="0.01"
            required
            // Held as a string, never as a number. A half-typed "12." is not a
            // number yet, and storing it as one would rewrite the field under
            // the cursor while someone is still typing into it.
            value={amount}
            onChange={(event) => setAmount(event.target.value)}
            aria-invalid={fieldErrors.amount ? true : undefined}
            aria-describedby={fieldErrors.amount ? 'amount-error' : undefined}
          />
          {/*
            No `max` attribute. The column's ceiling is 9999999999999999.99, and
            that number does not survive being written in JavaScript: it is past
            2^53, so the browser would parse the attribute as 1e16 and enforce a
            limit slightly different from the server's. An absent rule beats a
            lying one -- the server still refuses the amount, and says so here.
          */}
          <FieldMessages id="amount-error" messages={fieldErrors.amount} />
        </div>

        <div className="field field-currency">
          <label htmlFor="currency">Currency</label>
          <select
            id="currency"
            name="currency"
            required
            value={currency}
            onChange={(event) => setCurrency(event.target.value)}
            aria-invalid={fieldErrors.currency ? true : undefined}
            aria-describedby={
              fieldErrors.currency ? 'currency-error' : undefined
            }
          >
            {CURRENCIES.map((code) => (
              <option key={code} value={code}>
                {code}
              </option>
            ))}
          </select>
          <FieldMessages id="currency-error" messages={fieldErrors.currency} />
        </div>

        <div className="field field-description">
          <label htmlFor="description">Description</label>
          <input
            id="description"
            name="description"
            type="text"
            required
            // 500 to match [StringLength(500)] on the request. This one is
            // copied on purpose where the bounds above were not: maxLength
            // stops the typing rather than reporting it afterwards, and a
            // description silently truncated by a 400 after four hundred
            // characters is a genuinely annoying way to find out.
            maxLength={500}
            placeholder="Coffee and a croissant"
            value={description}
            onChange={(event) => setDescription(event.target.value)}
            aria-invalid={fieldErrors.description ? true : undefined}
            aria-describedby={
              fieldErrors.description ? 'description-error' : undefined
            }
          />
          <FieldMessages
            id="description-error"
            messages={fieldErrors.description}
          />
        </div>
      </div>

      <button type="submit" disabled={submitting} aria-busy={submitting}>
        {submitting ? 'Adding...' : 'Add transaction'}
      </button>
    </form>
  )
}
