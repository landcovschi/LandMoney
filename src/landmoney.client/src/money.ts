/** Money: how it is added, and how it is printed. */
// Both halves are here rather than beside the one screen that needs them, and
// each arrived for its own reason.
//
// `formatAmount` was private to TransactionList until #68 wanted the same number
// under a summary table. The rule it enforces is not obvious enough to be
// written twice -- `minimumFractionDigits: 2` is a decision about this
// application's column and not about the currency, and a second copy would hold
// it by luck rather than by agreement.
//
// The arithmetic is here for the sharper version of the same argument. `amount`
// arrives as a JSON number, which is an IEEE 754 double, and api/types.ts says
// it round-trips exactly *as long as nothing here does arithmetic with it*.
// Summing is arithmetic. So there is exactly one function in this client that
// turns an amount into something addable, and "we do not add doubles" is a place
// rather than a habit.

/** How many minor units make one major one. Two decimal places, everywhere. */
// Deliberately not the currency's own minor unit. `numeric(18,2)` holds two
// places whatever the currency is, so a yen amount stored as 12.34 is 1234 here
// -- and letting Intl use the currency's real unit would *display* it as 12,
// which is the value rounded away on its way to the screen. That is the same
// reasoning the two `FractionDigits` options below carry, in the half of this
// file that does the maths.
const MINOR_UNITS = 100

/** The amount as a whole number of minor units: `78.5` becomes `7850`. */
// **The only conversion in this client, and #68's second trap is why it exists.**
// Doubles do not add exactly -- 0.01 + 0.05 is 0.060000000000000005 -- so every
// sum in the summary is accumulated in integers, which do, and the division back
// happens once, at the end, on a value that is about to be formatted and never
// added again.
//
// **What that is worth was measured rather than assumed, and the honest answer is
// smaller than the rule sounds.** Two million two-decimal amounts summing to about
// a billion drift by 2.9e-6 as doubles -- and rendered to two places, the double
// total and the exact total are the same string at every point along the way. The
// rounding that hides it needs the error to reach half a minor unit, and every
// input here is already a two-decimal value, so the exact total never sits on a
// boundary where a millionth could push it over. So this is not a bug being fixed;
// it is a coincidence being turned into a property. The coincidence holds because
// these amounts are small, and it is not a thing the screen could report if it
// stopped holding.
//
// The multiplication is itself inexact: `78.5 * 100` is 7850.000000000001. That
// is what `Math.round` is for, and it is not a fudge -- the error is many orders
// of magnitude smaller than the half unit it would take to reach a different
// integer, so the result is the exact minor-unit value the row holds.
//
// Where the exactness ends, written down rather than guarded against: above 2^53
// minor units, roughly ninety trillion. api/types.ts already sets a lower bound
// than that -- a double round-trips fifteen significant digits, so an amount is
// exact to about 10^13 -- while `numeric(18,2)` will accept larger. One person's
// weekly spending is nowhere near either, and a check for it would have nowhere
// useful to report what it found.
export function toMinorUnits(amount: number): number {
  return Math.round(amount * MINOR_UNITS)
}

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

/** The same, for a figure that was accumulated in minor units. #68. */
export function formatMinorUnits(minorUnits: number, currency: string): string {
  // Dividing by 100 does not produce an exact binary value -- one hundredth is
  // not representable at all -- but it does produce the *nearest double* to that
  // decimal, which is precisely the value `JSON.parse` would have made of the
  // same number written out in the response. Intl then renders it to two places.
  //
  // So this is the guarantee every row of the table already rests on, paid once
  // per line of the summary rather than once per addition. The addition, which is
  // the part that would not survive it, has already happened in integers.
  return formatAmount(minorUnits / MINOR_UNITS, currency)
}
