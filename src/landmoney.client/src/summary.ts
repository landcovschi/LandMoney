import type { Transaction } from './api/types'
import { toMinorUnits } from './money'

/** Adding up a month's transactions. #68. */
// A module of its own rather than the bottom of MonthSummary.tsx, where this was
// written first. The lint rule that objected -- react(only-export-components),
// about fast refresh -- is the small reason; the larger one is that everything
// here is a pure function of its arguments with no clock, no fetch and no React
// in it, and that is exactly the half of #68 worth testing. This client still has
// no test framework to test it in, which #67 recorded for its own debounce. So
// the honest status is: checked by hand against the table it sits above, and
// shaped so that checking it by machine is one dependency away rather than a
// refactor away.

/** One category's share of one currency, for one month. */
// `category` is `string | null` and the null is a row rather than a gap: #68 says
// uncategorised is "a row like any other, and an honest one -- it is what the
// fallback and the abstentions produce". Three different things produce it, and
// the screen says so on that row: the categorizer abstained (#39), it was never
// called (an import, #62), or it was not running (#61). All three are the same
// null in the column, which is why the sentence has to list all three.
export interface CategoryTotal {
  category: string | null

  /** Minor units, never a fraction. See `toMinorUnits`. */
  totalMinorUnits: number

  count: number
}

/** Everything spent in one currency, in one month, broken down by category. */
// A currency is the outer grouping and not a column, and that is #68's first trap
// answered by the shape rather than by care. There is nowhere in this type to put
// a number that mixes two currencies -- every total it carries lives inside a
// `currency` -- so a screen built from it cannot add EUR to MDL by accident. A
// flat list of `{currency, category, total}` rows would carry exactly the same
// facts and would leave that addition one `reduce` away.
export interface CurrencyTotals {
  currency: string

  /** Largest first. */
  categories: readonly CategoryTotal[]

  /** The sum of the rows above. One currency, so this is a legal addition. */
  totalMinorUnits: number

  count: number
}

/** The word shown for the row that has no category. */
export const UNCATEGORISED = 'Uncategorised'

/**
 * Adds up the rows whose day falls in `month`, grouped by currency and category.
 *
 * `month` is a stored date's first seven characters: "2026-08".
 */
export function summariseMonth(
  transactions: readonly Transaction[],
  month: string,
): readonly CurrencyTotals[] {
  // A Map keyed on `string | null`, which JavaScript allows and which is worth
  // more here than it looks. The alternative is a sentinel string meaning "no
  // category", and a sentinel is a value a real category could one day collide
  // with; `null` cannot collide with anything in the closed list of eleven.
  const byCurrency = new Map<string, Map<string | null, CategoryTotal>>()

  for (const transaction of transactions) {
    // A prefix comparison on the stored string, and this is not a shortcut.
    // api/types.ts spells the trap out: `new Date("2026-08-19")` parses as *UTC*
    // midnight, so anyone west of UTC reads it as the 18th -- and every row
    // falling on the first of a month would be counted in the previous one. The
    // stored value is already "YYYY-MM-DD", so the month is its first seven
    // characters and no Date is ever constructed from a row.
    if (!transaction.occurredAt.startsWith(month)) {
      continue
    }

    let categories = byCurrency.get(transaction.currency)

    if (!categories) {
      categories = new Map()
      byCurrency.set(transaction.currency, categories)
    }

    const existing = categories.get(transaction.category)

    // The one addition in this whole feature, and it is on integers. Everything
    // in money.ts exists so that this line cannot be `+= transaction.amount`.
    if (existing) {
      existing.totalMinorUnits += toMinorUnits(transaction.amount)
      existing.count += 1
    } else {
      categories.set(transaction.category, {
        category: transaction.category,
        totalMinorUnits: toMinorUnits(transaction.amount),
        count: 1,
      })
    }
  }

  // The rows are built from the transactions and never from the eleven
  // categories, which answers #68's third acceptance test for free: a category
  // nobody spent anything on this month has no entry to render, so it is absent
  // rather than a zero. Starting from the vocabulary and filling it in would have
  // to remember to drop the empty ones, and would show eleven rows in a month
  // with three purchases in it.
  return [...byCurrency]
    .map(([currency, categories]) => {
      const rows = [...categories.values()].sort(byLargestFirst)

      return {
        currency,
        categories: rows,
        totalMinorUnits: rows.reduce((total, row) => total + row.totalMinorUnits, 0),
        count: rows.reduce((total, row) => total + row.count, 0),
      }
    })
    .sort(byBusiestFirst)
}

/** The current month as a stored date's first seven characters: "2026-08". */
// Read off the **local** clock, which is the calendar the reader is looking at.
// `OccurredAt` is a plain day with no zone (#17), so "this month" has to mean the
// month it is where they are; deriving it in UTC would put the first and the last
// day of every month in the wrong bucket for most of the world -- the same
// day-boundary problem #17 removed from storage, arriving in a filter.
//
// `getMonth` is zero-based, which is the one part of the Date API that catches
// everybody. `padStart` is what makes September "09" rather than "9": an unpadded
// prefix would match no stored date at all for the first nine months of a year,
// so the screen would report an empty month rather than a wrong one.
export function monthOf(now: Date): string {
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`
}

// Largest first, which is what the issue asks for, and then by name -- so two
// equal totals are ordered by a rule rather than by whichever row the Map happened
// to meet first. The uncategorised row takes its place among the others by its own
// total and is pinned to neither end: it is a row like any other.
function byLargestFirst(a: CategoryTotal, b: CategoryTotal): number {
  if (a.totalMinorUnits !== b.totalMinorUnits) {
    return b.totalMinorUnits - a.totalMinorUnits
  }

  return label(a.category).localeCompare(label(b.category))
}

// By how many transactions, and deliberately **not** by the total. Ordering the
// currency blocks by their totals would put 500 MDL above 400 EUR, which is the
// same mistake as adding them: it treats two numbers in different units as
// comparable, and nothing in this project converts between them. A count is a
// count in any currency, so it is the one quantity here that can order these.
function byBusiestFirst(a: CurrencyTotals, b: CurrencyTotals): number {
  if (a.count !== b.count) {
    return b.count - a.count
  }

  return a.currency.localeCompare(b.currency)
}

function label(category: string | null): string {
  return category ?? UNCATEGORISED
}
