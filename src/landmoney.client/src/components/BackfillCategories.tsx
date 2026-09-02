import { useEffect, useState } from 'react'
import { ApiError, backfillCategories, countBackfillable } from '../api/transactions'

/** What the panel knows at a given moment. */
// The same union shape ImportForm, ExportLabelled and TransactionList use, so that
// "asking" and "failed" cannot both be true and the compiler is what checks it.
type BackfillState =
  | { status: 'idle' }
  | { status: 'asking' }
  | { status: 'failed'; message: string }
  | { status: 'done'; marked: number }

/** How many rows a backfill would queue, once the server has been asked. #95. */
// **The three conditions this used to count in the browser are now one expression on
// the server**, `PendingCategorization.Backfillable`, which is the expression the
// POST acts through. That is the point of the move rather than a consequence of it:
// #93 wrote the same three rules here by hand and had to keep them equal to the
// server's by reading, and the failure of them drifting is a button offering a
// number nobody is going to act on.
//
// Null until the answer arrives, and null again if it does not. Both render nothing,
// which is what this card does when the count is zero -- so a failure costs a button
// that is not offered rather than a message about a chore.
type CountState = number | null

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
  version,
  onBackfilled,
}: {
  /** Bumped by every write, to ask again. #95. */
  // A number rather than the rows, for the reason MonthSummary takes one: #93
  // counted the loaded array, and paging means a loaded array is no longer the
  // table. The button would have offered the fifty rows on screen while the server
  // marked every uncategorised row there is -- and unlike a wrong total, that one is
  // a bill.
  version: number

  onBackfilled: () => void
}) {
  const [state, setState] = useState<BackfillState>({ status: 'idle' })
  const [uncategorised, setUncategorised] = useState<CountState>(null)

  // Re-asked on every write, because almost all of them move it: an import adds rows
  // with no category, a correction takes one away, a successful backfill takes all
  // of them away at once.
  useEffect(() => {
    const controller = new AbortController()

    countBackfillable(controller.signal)
      .then(({ count }) => setUncategorised(count))

      // Swallowed, for the reason the null state above gives: there is no action for
      // the reader to take, and the card simply does not appear. Reporting it would
      // put a message about a chore on the screen of somebody who came to record a
      // transaction.
      .catch(() => setUncategorised(null))

    return () => controller.abort()
  }, [version])

  // Hidden once there is nothing left, *unless* this run has something to report.
  // Without the second half the card vanishes at the moment it succeeds -- the list
  // is fetched again, the rows go into the queue, and the sentence saying what was
  // just spent disappears with them.
  //
  // `null` is included, and it is both "the count has not arrived yet" and "it could
  // not be fetched". A card offering to spend money on an unknown number of rows is
  // worse than no card.
  if (
    (uncategorised === null || uncategorised === 0) &&
    state.status !== 'done' &&
    state.status !== 'failed'
  ) {
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
        disabled={asking || !uncategorised}
        aria-busy={asking}
      >
        {asking
          ? 'Queueing...'
          : `Categorise ${uncategorised ?? 0} ${uncategorised === 1 ? 'transaction' : 'transactions'}`}
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
