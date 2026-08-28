import { useState } from 'react'
import { SOURCE_HUMAN, SOURCE_MODEL, SOURCE_RULES, type Transaction } from '../api/types'

interface CategoryCellProps {
  transaction: Transaction

  /** The closed list, from the server. Empty when the request for it failed. */
  categories: readonly string[]

  /** Resolves once the server has stored it. Rejects with the reason if it did not. */
  onChange: (category: string | null) => Promise<void>
}

/** What an HTML select yields for its blank option, and what null is sent as. */
// A select's value is a string and there is no way for it to be null, so the
// absence of a category needs a spelling. The empty string is the conventional one
// and it stops here: `toCategory` turns it back into null before it goes anywhere
// near the wire, because the server refuses "" on purpose -- a row whose category
// is neither a category nor absent is exactly what the closed vocabulary exists to
// prevent, and KnownCategoryAttributeTests asserts the refusal.
const NONE = ''

/**
 * One row's category: what it is, where it came from, and a way to disagree.
 */
// A component of its own rather than more JSX inside TransactionList, because it
// is the only thing on this screen with state per row -- a request in flight and a
// message about the last one. Keeping that in the list would mean two maps keyed
// by transaction id, and every render of any row would touch them; here each cell
// owns exactly its own, and React discards it with the row.
//
// What it deliberately does not own is the transaction. The stored value is always
// a prop, so the screen never shows a category the server has not confirmed for
// longer than the request takes -- `chosen` below is the one exception, and it is
// bounded by the same request.
export function CategoryCell({ transaction, categories, onChange }: CategoryCellProps) {
  // The value the user picked, held only while the request is in flight.
  //
  // This is #63's "optimistic update against the round trip", and the reason it is
  // worth the state: without it a controlled select snaps back to the old value the
  // instant it is changed and then changes again when the response lands, which
  // reads as the click not having registered. On a local Postgres that is a flicker;
  // on the deployed app the first request of a session pays a 23 second cold start.
  //
  // What makes it honest rather than a lie is what happens on failure -- it is
  // cleared, so the select visibly returns to what is actually stored, and the
  // message underneath says why. An optimistic update that keeps the new value after
  // a failed write is the version of this that is worse than no optimism at all.
  const [chosen, setChosen] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<string | null>(null)

  const stored = transaction.category
  const showing = saving && chosen !== null ? chosen : (stored ?? NONE)

  async function handleChange(value: string) {
    setChosen(value)
    setSaving(true)
    setMessage(null)

    try {
      await onChange(toCategory(value))
    } catch (error: unknown) {
      // The row keeps its stored value: `chosen` is dropped in the finally below
      // and the select is driven by the prop again. A 401 says the session has
      // ended, which App.tsx cannot act on from here without throwing away the
      // list -- so it is shown as the sentence it already is.
      // ApiError extends Error and every failure this client produces is one, so
      // `instanceof Error` covers both without naming the subclass. There is
      // nothing for a caller to do differently with an ApiError here -- the
      // fieldErrors it can carry would be keyed "category", and the control the
      // message belongs beside is the one right above this line.
      setMessage(
        error instanceof Error ? error.message : 'The correction was not saved.',
      )
    } finally {
      setSaving(false)
      setChosen(null)
    }
  }

  // No list to choose from, because the request for it failed. The category is
  // still worth showing, and a select with one option in it is worse than none:
  // it looks like the vocabulary is one word long.
  if (categories.length === 0) {
    return (
      <>
        {stored ? <Tag category={stored} source={transaction.categorySource} /> : <EmptyTag />}
      </>
    )
  }

  return (
    <div className="category">
      {/*
        aria-label rather than a <label>, and this is the one place on the screen
        where that is the right way round. A visible label per row would print
        "Category" twenty-one times under a column already headed Category; the
        column header does that job for a sighted reader and does not reach a
        select, which needs a name of its own. The description is in it because
        "Category" alone, announced twenty-one times, does not say which row.
      */}
      <select
        className="category-select"
        aria-label={`Category for ${transaction.description}`}
        aria-busy={saving}
        aria-invalid={message !== null}
        value={showing}
        disabled={saving}
        onChange={(event) => void handleChange(event.target.value)}
      >
        {/*
          First, and not last. Clearing a category is a real answer -- the same
          abstention the rules baseline produces -- so it belongs where somebody
          looking for it would expect the empty choice to be, at the top.
        */}
        <option value={NONE}>Uncategorised</option>

        {categories.map((category) => (
          <option key={category} value={category}>
            {category}
          </option>
        ))}

        {/*
          A category the server stored that is not in the list it serves. Only
          reachable if the two halves of the vocabulary have drifted -- which
          CategoriesTests turns red before it can be merged -- and worth rendering
          anyway, because a controlled select whose value matches no option shows
          the first option instead: the row would read "Uncategorised" while
          holding a category, which is a lie about stored data rather than a
          missing feature.
        */}
        {stored !== null && !categories.includes(stored) && (
          <option value={stored}>{stored}</option>
        )}
      </select>

      {/*
        The source badge, beside the control rather than inside it. This is what
        #63 exists for: without it a correction and a guess are the same word on
        the screen. Hidden while saving, because during the round trip the stored
        source is the *old* one and would name the wrong producer for the value
        being shown.
      */}
      {!saving && stored !== null && <SourceTag source={transaction.categorySource} />}

      {saving && (
        <span className="category-saving" role="status">
          Saving...
        </span>
      )}

      {message !== null && (
        // role="alert" so it is announced when it appears. It is not tied to the
        // select with aria-describedby, because that would have it read out again
        // on every focus of a control whose value is now correct.
        <span className="category-error" role="alert">
          {message}
        </span>
      )}
    </div>
  )
}

/** The category as a token, for the read-only fallback. */
function Tag({ category, source }: { category: string; source: string | null }) {
  return (
    <>
      <span className="tag">{category}</span>
      <SourceTag source={source} />
    </>
  )
}

function EmptyTag() {
  return <span className="tag tag-empty">Uncategorised</span>
}

/** Where the category came from: rules, model, or a person. */
// The word itself, not an icon and not a colour alone. Three sources is few enough
// to read, an icon needs a legend this screen has nowhere to put, and colour alone
// is not something everybody can tell apart.
//
// `data-source` rather than a class per value, so App.css styles the three it knows
// and an unrecognised fourth still renders with the base style instead of
// disappearing. The title is what makes the badge answer the question it raises --
// "rules" means nothing to somebody who has not read docs/evals.md.
function SourceTag({ source }: { source: string | null }) {
  if (source === null) {
    return null
  }

  return (
    <span className="tag tag-source" data-source={source} title={describe(source)}>
      {source}
    </span>
  )
}

// Each sentence says what produced the category and deliberately not how much to
// trust it. "model" is not a synonym for "probably right" -- docs/evals.md is where
// that number lives, with the caveat that matters more than the number.
function describe(source: string): string {
  if (source === SOURCE_HUMAN) {
    return 'Chosen by you.'
  }

  if (source === SOURCE_RULES) {
    return 'Guessed by matching words in the description.'
  }

  if (source === SOURCE_MODEL) {
    return 'Suggested by the model.'
  }

  // A fourth value. Only reachable if the categorizer gains a producer this
  // client has not been told about, and shown as it arrived rather than hidden:
  // the badge exists to say where a category came from, so the one case where the
  // answer is surprising is the last one to swallow.
  return `Recorded as "${source}", which this screen does not recognise.`
}

function toCategory(value: string): string | null {
  return value === NONE ? null : value
}
