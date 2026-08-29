import type { Transaction } from '../api/types'
import { formatAmount } from '../money'
import { CategoryCell } from './CategoryCell'

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
  | { status: 'ready'; transactions: readonly Transaction[] }

interface TransactionListProps {
  state: ListState
  onRetry: () => void

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
}

export function TransactionList({
  state,
  onRetry,
  categories,
  onChangeCategory,
}: TransactionListProps) {
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
    <table className="transactions" role="table">
      <caption>Everything recorded, newest first.</caption>

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
        </tr>
      </thead>

      <tbody role="rowgroup">
        {state.transactions.map((transaction) => (
          // key is the server's id, not the array index. React uses it to
          // decide which row is which between renders, and an index says every
          // row changed the moment one is inserted at the top -- which is
          // exactly what adding a transaction does here.
          <tr key={transaction.id} role="row">
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
          </tr>
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
  )
}

// formatAmount used to live here, as a private function with the whole argument
// for `minimumFractionDigits: 2` written above it. It moved to ../money.ts in #68,
// when the summary table needed the same number: the rule is a decision about this
// application's column rather than about the currency, and two copies of it would
// agree by luck. The reasoning moved with it and is not repeated here.
