import type { Transaction } from '../api/types'
import { formatMinorUnits } from '../money'
import { monthOf, summariseMonth, UNCATEGORISED } from '../summary'

interface MonthSummaryProps {
  /** The rows the list is showing. The same array, not a second fetch. */
  transactions: readonly Transaction[]
}

/** #68. Where this month's money went, by category, largest first. */
// **It adds up what is already on the screen, and there is no endpoint behind
// it.** The list is fetched whole, so the client holds every row already, and
// summing those is one pass over an array the page has anyway -- against a second
// round trip, a `GROUP BY` in EF, a third contract to keep in step with
// `api/types.ts` by hand, and a window in which the totals and the table below
// them could disagree because they were fetched at different moments. Here they
// cannot: the numbers come from the very array the table renders, which makes
// #68's first acceptance test -- "the totals equal the sums of the rows on the
// list" -- true by construction rather than by checking.
//
// **What that costs is the trap the issue names: it stops being fine silently.**
// `GET /api/transactions` has no paging and no limit, decided in #3 and written
// down there in those words. The day it grows one, this component keeps rendering
// and starts describing the page it was handed rather than the month -- with no
// error, no warning, and a number that looks entirely plausible. The fix on that
// day is a sum on the server in `decimal`, not a bigger page. It is written down
// here rather than built, because building it today would be a second contract
// guarding against a change nobody has made.
//
// Rendered only when the list is ready, which is why there is no loading state
// and no failed state in this file. The list underneath already says both of
// those things, and a second "Loading..." stacked above the first is noise.
export function MonthSummary({ transactions }: MonthSummaryProps) {
  // Read during render rather than held in state, so a tab left open across
  // midnight on the 31st is one reload away from being right instead of being
  // wrong until it is closed. Freezing it in `useState` is the version that
  // cannot recover.
  const now = new Date()
  const currencies = summariseMonth(transactions, monthOf(now))

  return (
    <section className="entry summary" aria-labelledby="summary-heading">
      {/*
        Built from `now` and never from a stored value, which is the safe half of
        the Date API: this object came from the local clock rather than from
        parsing a "2026-08-19", so there is no day to lose. `undefined` for the
        locale is the reader's own, the way every formatted value here is.
      */}
      <h2 id="summary-heading">
        {now.toLocaleDateString(undefined, { month: 'long', year: 'numeric' })}
      </h2>

      {currencies.length === 0 ? (
        // An empty month, which #68 asks to render as a month with nothing in it
        // rather than as a blank or a broken screen. It is also what a brand new
        // account sees, and the sentence is true of both without this having to
        // tell them apart.
        <p className="field-hint">Nothing recorded this month.</p>
      ) : (
        currencies.map((totals) => (
          <table className="summary-table" key={totals.currency}>
            {/*
              The currency is named on the block it governs rather than in a
              column, so every number below is read inside it. The total on this
              line is an addition within one currency, which is the only addition
              #68 allows -- and there is no line anywhere on this screen where a
              total of everything could go.

              The count sits between the code and the total on purpose. Intl
              renders a currency with no symbol as its code, so "MDL -- MDL
              290.00" was the first version of this line and read like a bug;
              €299.83 has the same shape and hides it. Putting the count in the
              middle keeps the code where it can be scanned down the page and
              leaves the repetition far enough apart to read as a sentence.
            */}
            <caption>
              {totals.currency} &mdash; {totals.count}{' '}
              {totals.count === 1 ? 'transaction' : 'transactions'},{' '}
              {formatMinorUnits(totals.totalMinorUnits, totals.currency)} in total
            </caption>

            {/*
              No explicit `role` on anything here, unlike TransactionList, and the
              difference is real rather than an inconsistency. That table is
              redrawn as a stack of cards below 640px, and changing an element's
              `display` takes its implicit role with it -- which is the only
              reason it writes its roles out. This one keeps three short columns
              at every width, so the roles it already has are the roles it keeps.
            */}
            <thead>
              <tr>
                <th scope="col">Category</th>
                <th scope="col" className="numeric">
                  Count
                </th>
                <th scope="col" className="numeric">
                  Total
                </th>
              </tr>
            </thead>

            <tbody>
              {totals.categories.map((row) => (
                // `?? ''` is a key nothing else can take: the empty string is
                // refused by the server's [Required] and is not one of the
                // eleven, so it belongs to the uncategorised row alone.
                <tr key={row.category ?? ''}>
                  {/*
                    A row header rather than a cell, so a screen reader announces
                    "groceries" beside each number instead of reading a column of
                    bare figures.

                    Not drawn as a .tag, although the list draws a category as
                    one. A pill is for a value appearing among other kinds of
                    content -- beside a description, under an input -- and here
                    the whole column is categories, so a column of pills would be
                    decoration repeating what the header already says. The one
                    exception is below, because the absence of a category is not
                    the same kind of thing as a category.
                  */}
                  <th scope="row">
                    {row.category ?? (
                      <span
                        className="summary-uncategorised"
                        title="No category: the categorizer had no idea, was not called, or was not running."
                      >
                        {UNCATEGORISED}
                      </span>
                    )}
                  </th>

                  <td className="numeric">{row.count}</td>

                  <td className="numeric">
                    {formatMinorUnits(row.totalMinorUnits, totals.currency)}
                  </td>
                </tr>
              ))}
            </tbody>

            {/*
              No footer repeating the caption's total. It would be the same number
              twice, and the caption is where it can sit beside the currency it
              belongs to -- which is the thing that stops it being read as a total
              of everything on the page.
            */}
          </table>
        ))
      )}
    </section>
  )
}
