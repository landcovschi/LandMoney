import { Fragment, useState } from 'react'
import type { Transaction, UpdateTransaction } from '../api/types'
import { formatAmount } from '../money'
import { CategoryCell } from './CategoryCell'
import { EditTransactionForm } from './EditTransactionForm'
import { RowActions } from './RowActions'

/**
 * What the list knows at a given moment.
 */
// A union rather than three separate booleans, so "loading, and also failed"
// cannot be written down at all. The C# parallel is returning a closed
// hierarchy instead of a bag of nullable fields: every branch has to be
// answered, and the compiler is the one checking that it was.
//
// It is also what makes the empty state honest. "There is nothing" and "we
// could not find out" are different facts, and a screen rendering the same
// blank for both is the failure #6 exists to prevent -- which is impossible to
// get wrong once they are different shapes rather than the same empty array.
export type ListState =
  | { status: 'loading' }
  | { status: 'failed'; message: string }
  | {
      status: 'ready'
      transactions: readonly Transaction[]

      /** The token for the next page, or null when this is the whole table. #95. */
      // Part of the list's state rather than a separate one, because "these rows"
      // and "where they stop" are one fact: a handler that replaces the rows and
      // forgets the cursor would offer to load a page it has already loaded. The
      // in-place handlers in App spread the object for exactly that reason.
      //
      // Null is the end and is knowable without asking: the server fetches one row
      // further than it returns, so it can say so inside the page that raised the
      // question. Without that the button could only disappear after a request that
      // came back empty.
      nextCursor: string | null
    }

interface TransactionListProps {
  state: ListState
  onRetry: () => void

  /** Appends the next page. Resolves when it is on screen; rejects with the reason. #95. */
  // A promise, like onChangeCategory and onDeleteTransaction, and for the same
  // reason: the button below holds its own in-flight and failed state, so it needs
  // to know when the request finished and whether it worked. App does not catch it.
  onLoadMore: () => Promise<void>

  /** The closed list of eleven, from the server. Empty if it could not be fetched. */
  // Passed down rather than fetched here, so there is one request for it per page
  // load instead of one per row -- and so the failure of that request is a fact the
  // application knows rather than something twenty-one components each discover.
  categories: readonly string[]

  /** Stores a correction. Resolves when the server has it; rejects with the reason. */
  // The promise is the contract. CategoryCell needs to know when the write
  // finished, and whether it worked, to decide what its own select shows -- so
  // this cannot be a fire-and-forget callback.
  onChangeCategory: (id: string, category: string | null) => Promise<void>

  /** Stores a correction to the four typed fields. Resolves when the server has it. #94. */
  onEditTransaction: (id: string, transaction: UpdateTransaction) => Promise<void>

  /** Removes the row. Resolves when it is gone; rejects with the reason. #94. */
  onDeleteTransaction: (id: string) => Promise<void>
}

export function TransactionList({
  state,
  onRetry,
  onLoadMore,
  categories,
  onChangeCategory,
  onEditTransaction,
  onDeleteTransaction,
}: TransactionListProps) {
  // Which row's edit form is open, or none. #94.
  //
  // One id rather than a set, so opening a second form closes the first: two
  // half-typed corrections on one screen is a way to save the wrong one, and
  // there is no reason to be editing two rows at once.
  //
  // It lives here rather than inside each row because that is the only place that
  // can enforce the "one at a time" above. The delete's own confirmation is the
  // opposite call and lives inside RowActions -- it is about one row and nothing
  // outside that row needs to know about it.
  //
  // The id can go stale, and harmlessly: a row deleted or filtered away while its
  // form is open leaves an id matching nothing, which renders nothing. Clearing it
  // on every list change would also close the form on every two-second poll.
  const [editing, setEditing] = useState<string | null>(null)

  // #95. What the "Load more" button is doing, and what to say if it failed.
  //
  // Local rather than lifted into ListState, which is the same call CategoryCell and
  // RowActions make for their own writes: nothing outside this element needs to know
  // that a page is on its way, and putting it in App would mean every component
  // under it re-rendering on a state change about one button.
  const [loadingMore, setLoadingMore] = useState(false)
  const [moreFailed, setMoreFailed] = useState<string | null>(null)

  if (state.status === 'loading') {
    return (
      <p className="list-status" role="status">
        Loading transactions...
      </p>
    )
  }

  if (state.status === 'failed') {
    return (
      <div className="banner banner-error" role="alert">
        <p>{state.message}</p>

        {/*
          A retry button, because the likeliest reason this failed during
          development is that the API was not running yet -- and starting it
          should not also mean reloading the page. It is the same idea as the
          fallback the .NET app will need in slice 4 when the categorizer is
          down: a failure the user can do something about beats one they can
          only look at.
        */}
        <button type="button" onClick={onRetry}>
          Try again
        </button>
      </div>
    )
  }

  if (state.transactions.length === 0) {
    return (
      <p className="list-status">
        Nothing recorded yet. The first transaction goes in the form above.
      </p>
    )
  }

  async function handleLoadMore() {
    setLoadingMore(true)
    setMoreFailed(null)

    try {
      await onLoadMore()
    } catch (error: unknown) {
      setMoreFailed(
        error instanceof Error ? error.message : 'Could not load any more transactions.',
      )
    } finally {
      // `finally`, so the button comes back whether the page arrived or not. Without
      // it a failed request leaves a permanently disabled control under an error
      // message telling the reader to try again.
      setLoadingMore(false)
    }
  }

  return (
    // Every role here is the one the element already has, so none of it does
    // anything at a width where the table is drawn as a table. They are
    // written down because below 640px App.css redraws each row as a card with
    // display: grid, and changing the display of a table element takes its
    // implicit role with it -- at which point scope="col" governs nothing and
    // the cells are announced as a flat list of values. An explicit role is
    // not affected by display, so the table stays a table in the accessibility
    // tree at every width while only its painting changes.
    //
    // The alternative is the usual one for this layout: drop the roles, and
    // give every <td> a data-label repeating its header for ::before to print
    // beside the value. That is a second copy of all four header strings with
    // nothing keeping them equal, and screen readers read generated content --
    // so the label arrives twice. The card in App.css prints no labels at all
    // instead, which is why it does not need them.
    <>
    <table className="transactions" role="table">
      {/*
        #95. It no longer is everything: the server answers fifty rows and the
        button below asks for the next fifty. The caption says what is on the
        screen rather than what is in the database, because a caption promising
        "everything" above a page is the silent half of paging -- the same failure
        #68's summary would have had, in a sentence instead of a number.
      */}
      <caption>
        {state.nextCursor === null
          ? 'Everything recorded, newest first.'
          : `The ${state.transactions.length} most recent, newest first.`}
      </caption>

      <thead role="rowgroup">
        <tr role="row">
          {/* scope tells a screen reader which cells each header governs.
              Two lines of markup, and without them the table is read as a
              list of unlabelled values. */}
          <th scope="col" role="columnheader">
            Date
          </th>
          <th scope="col" role="columnheader">
            Description
          </th>
          <th scope="col" role="columnheader">
            Category
          </th>
          <th scope="col" role="columnheader" className="numeric">
            Amount
          </th>

          {/*
            #94. A header with a word in it rather than an empty cell, because
            scope="col" governs cells and a column of controls with no name is
            announced as a column of unlabelled buttons. It is visually there and
            it is short on purpose -- the buttons underneath say what they do.
          */}
          <th scope="col" role="columnheader">
            Actions
          </th>
        </tr>
      </thead>

      <tbody role="rowgroup">
        {state.transactions.map((transaction) => (
          // #94. A Fragment because one transaction is now up to two <tr>s -- the
          // row, and the edit form under it when it is open. The key moves here
          // from the <tr> for the same reason it was there: React needs one key
          // per item in the map, and it is still the server's id rather than the
          // array index.
          //
          // Two rows rather than one row that turns into a form. Turning it into
          // one keeps the table narrow and puts four inputs and their messages
          // into cells whose widths were chosen for a date and an amount -- and
          // on a phone, where App.css draws each row as a grid, into a layout
          // that has nowhere to put them. A second row is a plain block in both.
          <Fragment key={transaction.id}>
          <tr role="row">
            <td role="cell">
              {/*
                The date printed exactly as it arrived. It is already
                "2026-08-19": a day anyone can read, and the one format that
                means the same thing in every country. Turning it into a Date to
                make it prettier is the bug named in TransactionContracts.cs --
                new Date("2026-08-19") is UTC midnight, so west of UTC it
                renders as the 18th.

                createdAt is the opposite case and *is* converted, because it is
                an instant rather than a day. It is also the tiebreak the server
                sorts on within one date, so it answers "why is this row above
                that one" -- worth having on hover, not worth a column.
              */}
              <time
                dateTime={transaction.occurredAt}
                title={`Added ${new Date(transaction.createdAt).toLocaleString()}`}
              >
                {transaction.occurredAt}
              </time>
            </td>

            <td role="cell">{transaction.description}</td>

            <td role="cell">
              {/*
                #63. The cell was a span holding the stored word; it is now a
                control, because the list rendered whatever the server had
                decided and there was no way to disagree with it. A correction
                made here is a labelled row produced by the one person who can
                judge it, during ordinary use -- which is the only route to
                labelled data in this project that is not somebody sitting down
                to do a chore.

                The per-row state lives inside CategoryCell rather than here.
                What that buys is written on the component; what it costs is
                that this list can no longer be read as a pure function of its
                props, which is why the boundary is a whole component and not a
                hook called in the middle of a map.
              */}
              <CategoryCell
                transaction={transaction}
                categories={categories}
                onChange={(category) => onChangeCategory(transaction.id, category)}
              />
            </td>

            <td role="cell" className="numeric">
              {formatAmount(transaction.amount, transaction.currency)}
            </td>

            <td role="cell" className="actions">
              <RowActions
                description={transaction.description}
                editing={editing === transaction.id}
                onEdit={() => setEditing(transaction.id)}
                onCancelEdit={() => setEditing(null)}
                onDelete={() => onDeleteTransaction(transaction.id)}
              />
            </td>
          </tr>

          {editing === transaction.id && (
            // A row of its own, spanning the whole table. `role="row"` and
            // `role="cell"` for the same reason every other one here carries
            // them: below 640px App.css changes what these elements are painted
            // as, and changing an element's display takes its implicit role with
            // it.
            //
            // colSpan is the table's column count and is the one number in this
            // file that has to be kept equal to something else -- the five <th>
            // above. Getting it wrong does not break the layout in any way a
            // browser reports; the form simply stops short of the last column.
            <tr className="row-edit" role="row">
              <td colSpan={5} role="cell">
                <EditTransactionForm
                  transaction={transaction}
                  onSave={async (edited) => {
                    await onEditTransaction(transaction.id, edited)

                    // Closed only on success, which is what leaves a refused
                    // change on the screen with the server's sentence beside the
                    // field it is about. Closing in a `finally` would throw away
                    // both the message and what was typed.
                    setEditing(null)
                  }}
                  onCancel={() => setEditing(null)}
                />
              </td>
            </tr>
          )}
          </Fragment>
        ))}
      </tbody>

      {/*
        No total row, and this is a rule rather than an omission: the table mixes
        currencies, so a column adding EUR to MDL produces a number that means
        nothing at all. A subtotal per currency would be correct, and #68 is where
        somebody wanted one -- it went into MonthSummary above this table rather
        than into a <tfoot> here. Two reasons. This table is every transaction ever
        recorded and the question was about the current month, so a footer would be
        answering a different one; and a total per currency inside a table sorted by
        date has no row to sit under. The rule this comment states is unchanged:
        nothing in this element adds two currencies together.
      */}
    </table>

    {/*
      #95. A button, and deliberately not an IntersectionObserver loading the next
      page as the reader nears the bottom.

      Infinite scrolling is what a list of this shape usually gets, and it loses on
      three things here. It cannot be reached from a keyboard at all, so the only way
      to see the older half of the table would be to scroll a mouse; it fetches
      whether or not anybody wanted more, which on a container that scales to zero
      (#61) is a request that wakes it up; and it is the one part of this feature
      with no test of any kind behind it -- this client still has no test framework
      (#67 recorded the same for its own debounce), so a scroll listener racing the
      two-second poll would be checked by reading and nothing else. A button is one
      element with one event, and pressing it is a decision the reader made.

      It is outside the <table> rather than in a <tfoot>. A footer row would be
      announced as a row of the table, which it is not -- there is no transaction in
      it -- and below 640px App.css redraws every row as a card, so it would be drawn
      as a card containing a button.
    */}
    {state.nextCursor !== null && (
      <p className="list-status">
        <button type="button" onClick={handleLoadMore} disabled={loadingMore} aria-busy={loadingMore}>
          {loadingMore ? 'Loading...' : 'Load more'}
        </button>
      </p>
    )}

    {/*
      The failure sits under the button rather than replacing the table, which is
      the same call CategoryCell makes for a correction: the rows on screen are
      still correct and still the ones the reader was reading, and the only thing
      that did not happen is the next page.
    */}
    {moreFailed !== null && (
      <div className="banner banner-error" role="alert">
        <p>{moreFailed}</p>
      </div>
    )}
    </>
  )
}

// formatAmount used to live here, as a private function with the whole argument
// for `minimumFractionDigits: 2` written above it. It moved to ../money.ts in #68,
// when the summary table needed the same number: the rule is a decision about this
// application's column rather than about the currency, and two copies of it would
// agree by luck. The reasoning moved with it and is not repeated here.
