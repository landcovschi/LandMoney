import { useEffect, useState } from 'react'
import { monthSummary } from '../api/transactions'
import type { MonthSummary as Totals } from '../api/types'
import { formatAmount } from '../money'
import { monthOf, UNCATEGORISED } from '../summary'

interface MonthSummaryProps {
  /**
   * Bumped by every write, to ask again. #95.
   */
  // A number rather than the rows, and that swap is the whole of what #95 did to
  // this component. It used to be handed the array the list was about to draw, which
  // made its totals and that table incapable of disagreeing; a paged list is no
  // longer the table, so the totals are a query of their own and this is how it
  // learns that one of them is stale.
  //
  // Every mutation bumps it -- a create, an import, a delete, an edit, and a
  // category correction, which changes no total but moves money between two rows of
  // this table. That last one is the one an author forgets, because it is the only
  // write that does not change what the list shows.
  version: number
}

/** What the panel knows at a given moment. */
// The same union shape the list and the other cards use. There is no 'failed' state
// rendered as a banner: see below.
type SummaryState =
  | { status: 'loading' }
  | { status: 'ready'; totals: Totals }
  | { status: 'failed' }

/** #68. Where this month's money went, by category, largest first. */
// **Added up by Postgres since #95, and the reason is in #68's own text**: it summed
// the rows the browser was holding, and wrote down that this "stops being fine
// silently" the day the list grows a page -- the component would keep rendering and
// start describing fifty transactions as though they were a month, with no error and
// a number that looks entirely plausible. The fix it named was "a sum on the server
// in decimal, not a bigger page", and that is what this now asks for.
//
// What is given up is the property that made #68's first acceptance test true by
// construction: the totals and the rows below them came from one array. They are two
// requests now, so a transaction saved between them appears in one and not the other
// until the next write bumps `version`. That window is milliseconds and the
// alternative is a screen that adds up part of a month.
export function MonthSummary({ version }: MonthSummaryProps) {
  // Read during render rather than held in state, so a tab left open across midnight
  // on the 31st is one reload away from being right instead of being wrong until it
  // is closed. Freezing it in `useState` is the version that cannot recover.
  const now = new Date()
  const month = monthOf(now)

  const [state, setState] = useState<SummaryState>({ status: 'loading' })

  // `month` is a dependency as well as `version`, which costs nothing and closes the
  // one case the version counter cannot: a tab open across midnight on the last of
  // the month re-renders for some other reason, `monthOf` answers the new month, and
  // the totals underneath it would otherwise still be September's.
  useEffect(() => {
    const controller = new AbortController()

    monthSummary(month, controller.signal)
      .then((totals) => setState({ status: 'ready', totals }))
      .catch(() => {
        if (!controller.signal.aborted) {
          setState({ status: 'failed' })
        }
      })

    return () => controller.abort()
  }, [month, version])

  // Nothing at all while it is on its way, and nothing at all if it did not arrive.
  //
  // The second half is the decision. Every other card in this application reports
  // its own failure, and this one must not: it is rendered above a table that is
  // fetched separately, so the ordinary way for this request to fail is that the
  // list's request failed too -- and the list says so, with a retry button. A banner
  // here would be the same sentence twice, above the one that can do something about
  // it. A summary that is missing is a screen with no summary on it, which is
  // legible on its own.
  if (state.status !== 'ready') {
    return null
  }

  const { currencies } = state.totals

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
        // rather than as a blank or a broken screen. App only mounts this when the
        // account has rows at all, so this sentence is about the month rather than
        // about a new account -- which is the distinction that keeps it from being
        // stacked on top of the list's "Nothing recorded yet".
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
              {formatAmount(totals.total, totals.currency)} in total
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

                  {/*
                    `formatAmount` and not the minor-unit version #68 wrote: the
                    number arrives as a decimal added by Postgres, so there is
                    nothing to convert back from. It is formatted and never added
                    to anything, which is the condition its exactness comes with.
                  */}
                  <td className="numeric">
                    {formatAmount(row.total, totals.currency)}
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
