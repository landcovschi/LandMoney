import { SOURCE_HUMAN, SOURCE_MODEL, SOURCE_RULES } from '../api/types'

/** Where the category came from: rules, model, or a person. */
// The word itself, not an icon and not a colour alone. Three sources is few enough
// to read, an icon needs a legend this screen has nowhere to put, and colour alone
// is not something everybody can tell apart.
//
// `data-source` rather than a class per value, so App.css styles the three it knows
// and an unrecognised fourth still renders with the base style instead of
// disappearing. The title is what makes the badge answer the question it raises --
// "rules" means nothing to somebody who has not read docs/evals.md.
//
// Its own file since #67, having lived inside CategoryCell for one issue. The badge
// now appears in two places that are not each other's parents -- beside a stored
// category in the table, and beside a suggestion under the description field -- and
// the alternative was importing a helper out of a component's file, which would
// have made CategoryCell look like the owner of something it merely uses first.
export function SourceTag({ source }: { source: string | null }) {
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
