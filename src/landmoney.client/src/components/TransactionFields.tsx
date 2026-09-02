import type { ReactNode } from 'react'
import type { FieldErrors } from '../api/types'
import { CURRENCIES } from '../fields'
import { FieldMessages } from './FieldMessages'

/** The four values a person types, held as strings the way the inputs hold them. */
// Every one of them a string, including the amount. A half-typed "12." is not a
// number yet, and storing it as one would rewrite the field under the cursor
// while somebody is still typing into it -- so the conversion happens once, at
// the edge, in whichever form is submitting.
export interface TransactionFieldValues {
  occurredAt: string
  amount: string
  currency: string
  description: string
}

interface TransactionFieldsProps {
  /**
   * What makes every `id` on this page unique.
   *
   * #94 put a second copy of these four fields on the screen -- the edit form
   * opens inside the list while the add form is still above it -- and an `id`
   * is document-wide. Two inputs called `amount` is not a lint error and not a
   * runtime one: the browser resolves a duplicate `id` to the *first* match, so
   * clicking the edit form's "Amount" label focuses the add form's input, and
   * `aria-describedby` points a screen reader at the wrong sentence. Both are
   * invisible until somebody uses a label or a screen reader, which is exactly
   * the class of bug that survives.
   */
  idPrefix: string

  values: TransactionFieldValues
  onChange: (values: TransactionFieldValues) => void

  /** What the server said was wrong, keyed the way `ValidationFilter<T>` keys it. */
  fieldErrors: FieldErrors

  /** Rendered under the description input. The suggestion badge, for the add form. */
  // A slot rather than a prop the component knows the meaning of, so this file
  // stays about four inputs and nothing else. The edit form passes nothing: #67's
  // suggestion is about a transaction that does not exist yet, and offering one
  // for a row whose category is already on screen would be advice about a
  // decision that has been made.
  children?: ReactNode
}

/**
 * The four fields a transaction is typed into, and their messages.
 */
// Extracted from TransactionForm in #94, when the edit form needed the same four.
// The alternative was a second copy, and it loses on the thing this repository
// keeps writing down: what drifts between two copies is not the values but *which
// rules are present* -- a `maxLength` added to one and forgotten on the other,
// with nothing reporting it. There is no rule here that is not in both, because
// there is only one of each.
//
// What it deliberately does not own is the submit button, the banner or the
// request. Those differ between the two forms -- one clears itself and stays, the
// other closes -- and a component that took callbacks for all of them would be
// the forms themselves with an awkward seam through the middle.
//
// What this validates and what it deliberately leaves alone:
//
// `required`, `step` and `maxLength` describe the *shape* of a value -- a number
// with at most two decimal places, a description that exists at all. They are
// written here because the browser enforces them before a request is made, which
// is faster than a round trip and costs nothing to keep.
//
// The *bounds* are not written here: five years back, one day ahead, the ceiling
// of numeric(18,2). Those are policy, they live on CreateTransactionRequest, and
// copying them into TypeScript would make two numbers that have to change
// together and will not. The server refuses them and the sentence it sends is
// shown beside the field -- which is the whole reason ValidationFilter camelCases
// its keys in the first place.
export function TransactionFields({
  idPrefix,
  values,
  onChange,
  fieldErrors,
  children,
}: TransactionFieldsProps) {
  // One helper rather than four setters, because the state lives in the parent
  // and there is no way to hand back half an object. The spread is what keeps
  // this from being a generic form library: four known keys, one at a time.
  function set(field: keyof TransactionFieldValues, value: string) {
    onChange({ ...values, [field]: value })
  }

  const id = (field: string) => `${idPrefix}-${field}`

  return (
    <div className="fields">
      <div className="field field-date">
        <label htmlFor={id('occurredAt')}>Date</label>
        <input
          id={id('occurredAt')}
          name="occurredAt"
          type="date"
          required
          value={values.occurredAt}
          onChange={(event) => set('occurredAt', event.target.value)}
          aria-invalid={fieldErrors.occurredAt ? true : undefined}
          aria-describedby={
            fieldErrors.occurredAt ? id('occurredAt-error') : undefined
          }
        />
        <FieldMessages
          id={id('occurredAt-error')}
          messages={fieldErrors.occurredAt}
        />
      </div>

      <div className="field field-amount">
        <label htmlFor={id('amount')}>Amount</label>
        <input
          id={id('amount')}
          name="amount"
          type="number"
          inputMode="decimal"
          // step is client-side scale validation for free, and it is the same
          // rule DecimalScaleAttribute enforces on the server: two decimal
          // places, because numeric(18,2) rounds a third one away in silence.
          step="0.01"
          min="0.01"
          required
          value={values.amount}
          onChange={(event) => set('amount', event.target.value)}
          aria-invalid={fieldErrors.amount ? true : undefined}
          aria-describedby={fieldErrors.amount ? id('amount-error') : undefined}
        />
        {/*
          No `max` attribute. The column's ceiling is 9999999999999999.99, and
          that number does not survive being written in JavaScript: it is past
          2^53, so the browser would parse the attribute as 1e16 and enforce a
          limit slightly different from the server's. An absent rule beats a
          lying one -- the server still refuses the amount, and says so here.
        */}
        <FieldMessages id={id('amount-error')} messages={fieldErrors.amount} />
      </div>

      <div className="field field-currency">
        <label htmlFor={id('currency')}>Currency</label>
        <select
          id={id('currency')}
          name="currency"
          required
          value={values.currency}
          onChange={(event) => set('currency', event.target.value)}
          aria-invalid={fieldErrors.currency ? true : undefined}
          aria-describedby={
            fieldErrors.currency ? id('currency-error') : undefined
          }
        >
          {CURRENCIES.map((code) => (
            <option key={code} value={code}>
              {code}
            </option>
          ))}

          {/*
            #94. A currency the row already holds that is not in the list above.
            Only reachable on the edit form, and only for a row imported from a
            CSV -- the import validates the *shape* of a currency and not its
            membership of these three, so a file carrying RON stores RON. Without
            this option a controlled select whose value matches nothing shows the
            first one instead, and opening the edit form would silently offer to
            change that row to EUR. The same trap CategoryCell answers for a
            category the server stored and no longer serves.
          */}
          {!CURRENCIES.includes(values.currency) && (
            <option value={values.currency}>{values.currency}</option>
          )}
        </select>
        <FieldMessages
          id={id('currency-error')}
          messages={fieldErrors.currency}
        />
      </div>

      <div className="field field-description">
        <label htmlFor={id('description')}>Description</label>
        <input
          id={id('description')}
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
          value={values.description}
          onChange={(event) => set('description', event.target.value)}
          aria-invalid={fieldErrors.description ? true : undefined}
          aria-describedby={
            fieldErrors.description ? id('description-error') : undefined
          }
        />
        <FieldMessages
          id={id('description-error')}
          messages={fieldErrors.description}
        />

        {children}
      </div>
    </div>
  )
}
