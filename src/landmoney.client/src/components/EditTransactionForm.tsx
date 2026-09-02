import { useState, type FormEvent } from 'react'
import { ApiError } from '../api/transactions'
import type { FieldErrors, Transaction, UpdateTransaction } from '../api/types'
import { unattachedMessages } from '../fields'
import {
  TransactionFields,
  type TransactionFieldValues,
} from './TransactionFields'

interface EditTransactionFormProps {
  /** The row as the server last reported it. What the fields start from. */
  transaction: Transaction

  /** Sends the correction. Rejects with an {@link ApiError} the server refused it with. */
  onSave: (transaction: UpdateTransaction) => Promise<void>

  /** Closes the form without sending anything. */
  onCancel: () => void
}

/**
 * The four fields of one existing row, open for correction. #94.
 */
// **Prefilled from the row, which is the whole of the concurrency answer.** The
// endpoint's comment is the long version: what #63 refused was a body that could
// carry an amount nobody was looking at, from whatever copy of the row a screen
// happened to be holding. Here every field that is sent is a field somebody read
// and either changed or chose to leave. Last write wins, and the writes are all
// deliberate.
//
// **The form is the confirmation.** #94 asks for both actions to be confirmable;
// for the delete that is a second click, and for this it is that nothing happens
// until Save -- a row cannot be edited by misclick, only opened.
export function EditTransactionForm({
  transaction,
  onSave,
  onCancel,
}: EditTransactionFormProps) {
  // Seeded from the props once and then owned here. Deliberately *not* kept in
  // step with the prop afterwards: the polling in App.tsx replaces the whole list
  // every two seconds while anything is uncategorised, so a form that followed
  // its prop would have the category sweep rewrite what somebody is typing.
  //
  // What that costs is that a change made elsewhere while this is open is
  // overwritten on save, which is the last-write-wins the endpoint argues for.
  // What it buys is the field not moving under the cursor, which happens every
  // few seconds rather than never.
  const [values, setValues] = useState<TransactionFieldValues>({
    occurredAt: transaction.occurredAt,

    // Back to a string, because that is what an input holds -- and `String` and
    // not `toFixed(2)`: an amount stored as 78.50 arrives here as the number
    // 78.5, and typing a 2 into "78.50" is a different thing from typing it into
    // "78.5". The server rounds neither, and #62 records that the two are the
    // same decimal.
    amount: String(transaction.amount),

    currency: transaction.currency,
    description: transaction.description,
  })

  const [saving, setSaving] = useState(false)
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    setSaving(true)
    setFieldErrors({})
    setFormError(null)

    try {
      await onSave({
        occurredAt: values.occurredAt,

        // The one conversion, at the edge, exactly as the add form does it.
        // `Number` and not `parseFloat`, for the reason written there.
        amount: Number(values.amount),

        currency: values.currency,
        description: values.description,
      })

      // Nothing is cleared and nothing is reset: on success this component is
      // unmounted by the row closing its form, so there is no state left to put
      // back. The add form clears two fields instead, because it stays.
    } catch (error) {
      if (error instanceof ApiError) {
        setFieldErrors(error.fieldErrors)

        const hasFieldMessages = Object.keys(error.fieldErrors).length > 0
        setFormError(hasFieldMessages ? null : error.message)
      } else {
        setFormError('Something went wrong while saving the change.')
      }

      // Only on the failure path. On success the unmount above makes this a
      // setState into a component that is going away.
      setSaving(false)
    }
  }

  const bannerMessages = formError
    ? [formError]
    : unattachedMessages(fieldErrors)

  return (
    <form className="entry entry-inline" onSubmit={handleSubmit}>
      {/*
        A heading, so the form is not four inputs that appeared under a row with
        nothing saying what they are. h3 rather than h2: the list's caption is
        what this sits inside, and skipping a level is the accessibility
        equivalent of a missing label.
      */}
      <h3>Correct this transaction</h3>

      {bannerMessages.length > 0 && (
        <p className="banner banner-error" role="alert">
          {bannerMessages.join(' ')}
        </p>
      )}

      {/*
        The id prefix is the row's own id, so two edit forms could be open at
        once without colliding -- and, far more to the point, so none of these
        collides with the add form still sitting at the top of the page. The
        long version is on TransactionFields.
      */}
      <TransactionFields
        idPrefix={`edit-${transaction.id}`}
        values={values}
        onChange={setValues}
        fieldErrors={fieldErrors}
      />

      {/*
        No category field, and that absence is the endpoint's shape rather than a
        simplification. A client that could send a category could send a source,
        and a row claiming `model` because a browser said so is exactly the hole
        `category_source` was added in #59 to close. The dropdown in the row
        above is still the only way to set one, and it files the change as
        `human`.

        What this form does do to the category is written on the server: changing
        the description, the amount or the currency clears a *predicted* one and
        asks again, because it was a guess about text that is no longer there. A
        category a person chose survives, and correcting a mistyped date changes
        nothing at all.
      */}
      <p className="entry-note">
        Changing the description, the amount or the currency asks the categorizer
        again. A category you chose yourself is kept.
      </p>

      <div className="entry-actions">
        <button type="submit" disabled={saving} aria-busy={saving}>
          {saving ? 'Saving...' : 'Save changes'}
        </button>

        {/*
          type="button", or it submits: a <button> inside a <form> defaults to
          type="submit", which would make Cancel the most expensive control on
          the screen.
        */}
        <button
          type="button"
          className="secondary"
          onClick={onCancel}
          disabled={saving}
        >
          Cancel
        </button>
      </div>
    </form>
  )
}
