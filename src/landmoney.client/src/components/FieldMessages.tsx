import type { ReactElement } from 'react'

/** The server's messages about one field, or nothing at all. */
// Moved out of TransactionForm in #52, when LoginForm needed the same thing. A
// second copy was the alternative and it is the one that rots: the two would drift
// on the aria wiring, which is the part nobody re-checks because nobody sees it.
// A list rather than a single line, because the errors dictionary holds an
// array per field and more than one rule can fail at once: an amount of -0.005
// breaks both [Range] and [DecimalScale], and showing one of the two would send
// someone round the loop twice.
export function FieldMessages({
  id,
  messages,
}: {
  id: string
  messages?: readonly string[]
}): ReactElement | null {
  if (!messages || messages.length === 0) {
    return null
  }

  return (
    <ul className="field-error" id={id}>
      {messages.map((message) => (
        <li key={message}>{message}</li>
      ))}
    </ul>
  )
}
