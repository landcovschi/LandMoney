import { useState } from 'react'

interface RowActionsProps {
  /** What the row is about, so the buttons can say which row they belong to. */
  description: string

  /** Is the edit form for this row open? The button becomes its Cancel. */
  editing: boolean

  onEdit: () => void
  onCancelEdit: () => void

  /** Resolves once the row is gone. Rejects with the reason if it is not. */
  onDelete: () => Promise<void>
}

/**
 * The two things that can be done to a whole row: change it, or remove it. #94.
 */
// **The delete is two clicks, and the confirmation is inline rather than
// `window.confirm`.** #94 asks for a confirmation because a table of a year of
// history is one stray tap away from a bad afternoon, and the browser's own
// dialog is the obvious way to get one for free. It loses on three things and the
// first is the one that matters: it blocks the whole page synchronously, so the
// polling in App.tsx and any request in flight stop until somebody reads it. The
// other two are that it cannot be styled at all -- a system dialog quoting a
// shop's name is a jarring thing to meet inside a spending list -- and that
// several browsers now allow it to be suppressed for the rest of a page's life,
// which turns "are you sure" into "deleted" with no way to know it happened.
//
// What replaces it is one piece of state. The button becomes a sentence and two
// choices, and nothing is sent until the second click. Cancel is what the button
// was, so a misclick costs one more click and never a row.
//
// **Deliberately not a countdown or an undo.** An undo would mean the row still
// existing somewhere, which is the soft delete the endpoint argues against, and a
// timer that fires on its own is a delete nobody clicked.
export function RowActions({
  description,
  editing,
  onEdit,
  onCancelEdit,
  onDelete,
}: RowActionsProps) {
  const [confirming, setConfirming] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [message, setMessage] = useState<string | null>(null)

  async function handleDelete() {
    setDeleting(true)
    setMessage(null)

    try {
      await onDelete()

      // Deliberately no setState after this point on the success path. The row is
      // gone, so this component is about to be unmounted by the list re-rendering
      // without it -- and writing state into a component that is going away is
      // the "cannot update an unmounted component" warning, earned honestly.
      // `deleting` stays true for the frames until then, which is what keeps the
      // button from being clickable twice.
      return
    } catch (error: unknown) {
      // The row is still there, so the message belongs beside it rather than in a
      // banner at the top of the page -- which is #63's mislocated-message
      // mistake, and the reason CategoryCell renders its own failures in the cell.
      // A 404 arrives here too, and its sentence is the server's: somebody else's
      // row and a row that was already deleted are the same answer on purpose.
      setMessage(
        error instanceof Error ? error.message : 'The row was not deleted.',
      )

      setDeleting(false)
      setConfirming(false)
    }
  }

  return (
    <div className="row-actions">
      {!confirming && (
        <>
          {/*
            The edit button becomes the cancel for its own form. One control
            rather than two, because "Edit" on a row whose edit form is already
            open below it is a button that does nothing, and a reader has to try
            it to find that out.
          */}
          <button
            type="button"
            className="link"
            onClick={editing ? onCancelEdit : onEdit}
            disabled={deleting}
          >
            {editing ? 'Cancel' : 'Edit'}
          </button>

          <button
            type="button"
            className="link link-danger"
            onClick={() => setConfirming(true)}
            disabled={deleting}
            // The description is in the accessible name for the same reason it is
            // in CategoryCell's: a column of twenty buttons all called "Delete"
            // tells a screen reader nothing about which row is about to go.
            aria-label={`Delete ${description}`}
          >
            Delete
          </button>
        </>
      )}

      {confirming && (
        // role="group" with a name, so the two buttons are announced as one
        // decision about one row rather than as two loose controls that happen to
        // be next to each other.
        <span
          className="row-confirm"
          role="group"
          aria-label={`Delete ${description}?`}
        >
          <span className="row-confirm-question">Delete?</span>

          <button
            type="button"
            className="link link-danger"
            onClick={() => void handleDelete()}
            disabled={deleting}
            aria-busy={deleting}
          >
            {deleting ? 'Deleting...' : 'Yes, delete'}
          </button>

          <button
            type="button"
            className="link"
            onClick={() => setConfirming(false)}
            disabled={deleting}
          >
            Keep
          </button>
        </span>
      )}

      {message !== null && (
        // role="alert" so it is announced when it appears, and not tied to either
        // button with aria-describedby -- that would read it out again on every
        // focus of a control whose last press is no longer what failed.
        <span className="row-error" role="alert">
          {message}
        </span>
      )}
    </div>
  )
}
