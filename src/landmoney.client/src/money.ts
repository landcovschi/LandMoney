/** Money: how it is printed. */
// `formatAmount` was private to TransactionList until #68 wanted the same number
// under a summary table. The rule it enforces is not obvious enough to be written
// twice -- `minimumFractionDigits: 2` is a decision about this application's column
// and not about the currency, and a second copy would hold it by luck rather than by
// agreement.
//
// **The other half of this file is gone, and its absence is the decision worth
// reading.** #68 added a month up in the browser, so `toMinorUnits` existed to turn
// an amount into something addable and `formatMinorUnits` to turn it back --
// integers, because doubles do not add exactly. #95 moved the sum into Postgres,
// where it is a `numeric` addition and the question does not arise, so both were
// deleted rather than left for a caller that no longer exists.
//
// What that means for the rule those functions carried: **nothing in this client
// adds two amounts together any more.** It is not enforced by anything here -- it is
// enforced by there being no total on the screen that this side computes. The day
// one comes back, the honest fix is another query and not another helper.

// One Intl.NumberFormat per currency, kept rather than rebuilt per row.
// Constructing one is the expensive part -- it loads the locale's data -- and a
// hundred rows would otherwise build a hundred of them on every render. The C#
// parallel is holding on to a NumberFormatInfo instead of calling
// CultureInfo.GetCultureInfo inside the loop.
const formatters = new Map<string, Intl.NumberFormat>()

/** One amount, in its own currency, at two decimal places. */
export function formatAmount(amount: number, currency: string): string {
  let formatter = formatters.get(currency)

  if (!formatter) {
    // The constructor throws RangeError on a currency that is not three
    // ASCII letters, and cannot be reached with one: the server validates
    // "^[A-Za-z]{3}$" and upper-cases before storing. Note what it does *not*
    // check -- that the code is a real ISO 4217 currency. "XYZ" is stored and
    // formatted here as "XYZ 12.34", which is the honest thing to do with it.
    formatter = new Intl.NumberFormat(undefined, {
      style: 'currency',
      currency,

      // Both pinned to 2, rather than left to the currency's own minor unit,
      // which is what style: 'currency' uses by default. The default is right
      // about the currency and wrong about this column: the yen has zero
      // decimal places, so an amount stored as 12.34 would be *displayed* as
      // 12 -- the value rounded away on its way to the screen, which is exactly
      // what #6 forbids. numeric(18,2) holds two places whatever the currency
      // is, so the screen shows two.
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    })

    formatters.set(currency, formatter)
  }

  // Formatting, not arithmetic. The double that came out of JSON.parse is
  // rendered to two decimal places and never added to anything -- which is the
  // condition under which its exactness holds.
  return formatter.format(amount)
}
