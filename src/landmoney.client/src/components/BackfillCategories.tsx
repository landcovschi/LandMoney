import { useState } from 'react'
import { ApiError, backfillCategories } from '../api/transactions'
import type { Transaction } from '../api/types'

/** What the panel knows at a given moment. */
// The same union shape ImportForm, ExportLabelled and TransactionList use, so that
// "asking" and "failed" cannot both be true and the compiler is what checks it.
type BackfillState =
  | { status: 'idle' }
  | { status: 'asking' }
  | { status: 'failed'; message: string }
  | { status: 'done'; marked: number }

/** Rows with no category that nothing is currently going to categorise. #93. */
// **This has to select exactly what PendingCategorization.Backfillable selects**, or
// the button offers a number the server does not act on. The three conditions are
// the same three, in the same order:
//
//   category === null        nothing has decided one
//   categorySource !== human nobody has labelled it by hand
//   !categoryPending         the sweep is not already going to get to it
//
// The last one is what makes the count fall as the sweep works rather than
// double-counting rows already in the queue: `categoryPending` is the server's own
// "owed and still within the cap", so a row abandoned at the cap reads false here
// and is offered again, which is the whole of "anything the categorizer missed
// while it was down".
//
// A row a person deliberately *cleared* is counted, because it has no category and
// no source. That is #63's known hole and it is accepted rather than hidden -- see
// PendingCategorization.Backfillable for the argument. What it means here is that
// the number can include a row somebody meant to leave blank, and that the way to
// leave it blank again is to clear it again.
// Not exported, unlike #68's `summariseMonth`, and the difference is that there is
// nothing yet to export it *to*: this client still has no test framework (#67
// recorded the same for its own debounce), and an export with no importer is what
// the fast-refresh lint rule objects to. It is a function rather than an inline
// filter so that the three conditions have a name and a comment, which is the half
// that matters -- the day this client gains a test runner, moving it out is a line.
function countUncategorised(transactions: readonly Transaction[]): number {
  return transactions.filter(
    (transaction) =>
      transaction.category === null &&
      transaction.categorySource !== 'human' &&
      !transaction.categoryPending,
  ).length
}

/** #93. Asks the categorizer about the rows nothing has asked about yet. */
// A card of its own, under the import and the export, and the placement is the
// argument: #62 imports rows with no category and says so, and this is what does
// something about them, so it reads in the order the work happens.
//
// **The count is on the screen before the button is pressed**, which is #93's third
// trap answered rather than met: "whatever runs this should know how many rows it is
// about to pay for before it starts". Every row marked here is one model call the
// sweep will make, at about 0.62 US cents (#87), so a year of imported statements is
// a couple of dollars -- small, and not something to discover afterwards.
//
// It renders nothing at all when there is nothing to do. A permanent card saying "0
// rows" would be a standing invitation to press a button that does nothing, on the
// screen of somebody whose transactions are all categorised, which is the ordinary
// state of this application.
export function BackfillCategories({
  transactions,
  onBackfilled,
}: {
  transactions: readonly Transaction[]
  onBackfilled: () => void
}) {
  const [state, setState] = useState<BackfillState>({ status: 'idle' })

  const uncategorised = countUncategorised(transactions)

  // Hidden once there is nothing left, *unless* this run has something to report.
  // Without the second half the card vanishes at the moment it succeeds -- the list
  // is fetched again, the rows go into the queue, and the sentence saying what was
  // just spent disappears with them.
  if (uncategorised === 0 && state.status !== 'done' && state.status !== 'failed') {
    return null
  }

  async function handleBackfill() {
    setState({ status: 'asking' })

    try {
      const { marked } = await backfillCategories()

      setState({ status: 'done', marked })

      // The list, so the rows show "Categorizing..." straight away and App's poll
      // takes it from there. Without this the queue fills and the screen says
      // nothing until something else happens to reload.
      onBackfilled()
    } catch (error: unknown) {
      setState({
        status: 'failed',
        message:
          error instanceof ApiError
            ? error.message
            : 'Could not queue the transactions for categorising.',
      })
    }
  }

  const asking = state.status === 'asking'

  return (
    <section className="entry backfill" aria-labelledby="backfill-heading">
      <h2 id="backfill-heading">Categorise what is left</h2>

      <p className="field-hint">
        Imported rows arrive without a category, and a row the categorizer could
        not be reached about keeps its place in the queue only for a while. This
        asks about every transaction that has none -- one question each, answered
        in the background over the next few minutes.
      </p>

      <button
        type="button"
        onClick={handleBackfill}
        disabled={asking || uncategorised === 0}
        aria-busy={asking}
      >
        {asking
          ? 'Queueing...'
          : `Categorise ${uncategorised} ${uncategorised === 1 ? 'transaction' : 'transactions'}`}
      </button>

      {state.status === 'failed' && (
        <div className="banner banner-error" role="alert">
          <p>{state.message}</p>
        </div>
      )}

      {/*
        role="status" rather than "alert", matching ImportReport and ExportReport: a
        finished backfill is information, and the banner above is the one that
        interrupts.
      */}
      {state.status === 'done' && (
        <div className="import-report" role="status">
          {state.marked === 0 ? (
            <p>
              <strong>Nothing to queue.</strong> Every transaction either has a
              category or is already waiting for one.
            </p>
          ) : (
            <p>
              <strong>
                {state.marked} {state.marked === 1 ? 'transaction' : 'transactions'}{' '}
                queued.
              </strong>{' '}
              Their categories appear in the list as they arrive. Ones the
              categorizer has no opinion about stay blank, and asking again will
              ask about those too.
            </p>
          )}
        </div>
      )}
    </section>
  )
}
