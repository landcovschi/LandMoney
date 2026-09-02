/** Which month the summary is about. #68, and what is left of it after #95. */
// **The adding up used to be here and is now Postgres's.** #68 summed the month in
// the browser and argued for it: the list was fetched whole, so the totals and the
// rows below them were one array counted twice and could not disagree. Its own
// comment named what would end that -- "the day `GET /api/transactions` grows a
// page, this component keeps rendering and starts describing the page it was handed
// rather than the month" -- and #95 is that day.
//
// So `summariseMonth`, `CategoryTotal` and `CurrencyTotals` moved: the first into
// `MonthSummaryAsync`, the other two into `api/types.ts` as shapes the server
// serialises. `money.ts` lost `toMinorUnits` and `formatMinorUnits` with them, which
// is the larger half of the deletion -- there is no addition left anywhere in this
// client, so there is nothing here that could add two doubles together.
//
// What stays is the one thing the server cannot answer: which month it is where the
// reader is.

/** The word shown for the row that has no category. */
// It stays on this side although the row it labels now arrives from the server,
// deliberately: the server sends `null`, which is the fact, and "Uncategorised" is a
// word on a screen. Sending the word would make a display decision travel over the
// wire and would put an English string into a response the eval tooling also reads.
export const UNCATEGORISED = 'Uncategorised'

/** The current month as a stored date's first seven characters: "2026-08". */
// Read off the **local** clock, which is the calendar the reader is looking at.
// `OccurredAt` is a plain day with no zone (#17), so "this month" has to mean the
// month it is where they are; deriving it in UTC would put the first and the last
// day of every month in the wrong bucket for most of the world -- the same
// day-boundary problem #17 removed from storage, arriving in a filter.
//
// #95 moved the sum to the server and left this here, which is the half of that
// decision worth reading: the server has a clock and it is the wrong one. Its
// container may sit in any region, so a month picked there would be somebody else's
// month. This value is what the request carries.
//
// `getMonth` is zero-based, which is the one part of the Date API that catches
// everybody. `padStart` is what makes September "09" rather than "9": the server
// refuses a month that is not exactly seven characters, so an unpadded one would be
// a 400 for the first nine months of every year rather than a wrong answer -- which
// is the better of the two failures and still a failure.
export function monthOf(now: Date): string {
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`
}
