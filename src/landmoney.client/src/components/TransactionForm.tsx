import { useState, type FormEvent } from 'react'
import { useCategorySuggestion } from '../hooks/useCategorySuggestion'
import { ApiError } from '../api/transactions'
import type { FieldErrors, NewTransaction } from '../api/types'
import { SourceTag } from './SourceTag'
import { CURRENCIES, unattachedMessages } from '../fields'
import {
  TransactionFields,
  type TransactionFieldValues,
} from './TransactionFields'

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

// The four inputs moved to TransactionFields in #94, when the edit form needed
// the same four and a second copy of them would have been two sets of rules that
// drift. What stayed here is everything that is about *adding*: the empty start,
// the clearing, the suggestion, and the button.
export function TransactionForm({ onSubmit }: TransactionFormProps) {
  // One object rather than the four useStates that were here, and the reason is
  // the shared component rather than a change of mind: TransactionFields takes
  // and returns the whole set, so four setters would be four lines assembling an
  // object on every keystroke. #6's "boring" still applies -- there is no generic
  // update helper, only a spread of four known keys, and it lives in one place.
  const [values, setValues] = useState<TransactionFieldValues>({
    occurredAt: today(),
    amount: '',
    currency: CURRENCIES[0],
    description: '',
  })

  // #67. What the categorizer would say about what is being typed, asked once the
  // typing stops. It is display only: nothing here is sent with the transaction and
  // nothing waits for it, so a suggestion cannot delay or block a save. The row's
  // category is decided on the server when the transaction is created, from the
  // same three values -- see the endpoint for why it is asked twice rather than
  // sent back from here.
  const suggestion = useCategorySuggestion(
    values.description,
    values.amount,
    values.currency,
  )

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
        occurredAt: values.occurredAt,

        // The amount is held as text and becomes a number exactly once, here.
        // `Number` and not `parseFloat`: parseFloat reads as far as it
        // understands and silently ignores the rest, so "12abc" becomes 12,
        // where Number returns NaN. The input's own `required` and `step` have
        // already refused both, which is why this can be the only guard.
        amount: Number(values.amount),

        currency: values.currency,
        description: values.description,
      })

      // Cleared on success, and only these two. The date and the currency stay
      // put: a week of spending is typed in one sitting, mostly on the same day
      // and in the same currency, and re-picking both every time is the small
      // tax that stops an app being used weekly -- which is the habit slice 4's
      // evals depend on existing.
      setValues((current) => ({ ...current, amount: '', description: '' }))
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

  const bannerMessages = formError
    ? [formError]
    : unattachedMessages(fieldErrors)

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

      <TransactionFields
        idPrefix="add"
        values={values}
        onChange={setValues}
        fieldErrors={fieldErrors}
      >
        {/*
          #67. The one visibly intelligent thing in this application, said out
          loud before the transaction exists rather than after it is a row in a
          table.

          role="status" and not role="alert": it is announced politely, after
          whatever is being typed, because it is not an error and interrupting
          somebody mid-word to tell them about a guess is worse than saying
          nothing. Deliberately not tied to the input with aria-describedby,
          which would read it out again on every focus.

          **The paragraph is always in the document and only its contents come
          and go**, which looks like an empty element for nothing and is the
          whole of whether the announcement happens. A live region has to exist
          before the text appears in it -- a region and its content inserted in
          one go is announced by some screen readers and silently missed by
          others, which is the same failure as having no region at all and is
          harder to notice. App.css collapses its margin while it is empty.

          Nothing is rendered while a request is in flight, and nothing at all is
          rendered when one fails -- the reasoning is on SuggestionState. The
          previous suggestion stays visible while a newer one is on the way,
          which is the one place this shows something a beat out of date: it is
          about the description as it was 400 ms ago. Clearing it per keystroke
          was the alternative and it flickers, and the answer that matters is the
          server's at save time rather than this one.

          It is passed as a child of TransactionFields rather than living there,
          because the edit form must not show one: #67's suggestion is about a
          transaction that does not exist yet, and offering one for a row whose
          category is already on screen is advice about a decision already made.
        */}
        <p className="suggestion" role="status">
          {suggestion.status === 'suggested' && (
            <>
              <span className="suggestion-label">Suggested</span>
              <span className="tag">{suggestion.category}</span>
              <SourceTag source={suggestion.source} />
            </>
          )}

          {/*
            "No idea" is a normal answer -- the rules decline on roughly a third
            of the labelled set -- so it is shown rather than treated as nothing
            having happened. The badge names who declined, which is the
            difference between a baseline that does not know this shop and a
            model that does not.
          */}
          {suggestion.status === 'unknown' && (
            <>
              <span className="suggestion-label">No suggestion</span>
              <span className="tag tag-empty">Uncategorised</span>
              <SourceTag source={suggestion.source} />
            </>
          )}

          {suggestion.status !== 'none' && (
            <span className="suggestion-note">
              A guess, applied when you save. You can change it in the list.
            </span>
          )}
        </p>
      </TransactionFields>

      <button type="submit" disabled={submitting} aria-busy={submitting}>
        {submitting ? 'Adding...' : 'Add transaction'}
      </button>
    </form>
  )
}
