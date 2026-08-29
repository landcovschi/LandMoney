import { useEffect, useState } from 'react'
import { suggestCategory } from '../api/transactions'
import type { CategorySuggestionQuery } from '../api/types'

/** How long the typing has to stop before anything is asked. */
// #67: a request per keystroke against a paid model is a bill. Four hundred
// milliseconds is roughly a pause rather than a gap between two letters, so
// "Lidl" is one request and not four -- and the number is short enough that the
// answer arrives while the description is still what was typed.
//
// It is not a rate limit and must not be mistaken for one. The only thing standing
// between this screen and an unbounded number of model calls is that a person
// types slowly; the server-side note on CategorySuggestionRequest says the same
// thing from the other end, and says what to do about it if that stops being
// enough.
const DEBOUNCE_MS = 400

/** How much description is worth asking about. */
// Two letters is not a merchant name, it is the beginning of one, and the answer
// to it would be replaced before it could be read. The server would answer a
// single character perfectly happily -- it is a valid description -- so this is a
// judgement about what is worth a round trip rather than a rule about what is
// allowed.
const MIN_DESCRIPTION_LENGTH = 3

/** What the form knows about a suggestion for what is currently typed. */
// Three states and not four: there is deliberately no `failed`. A suggestion that
// could not be fetched shows exactly what a suggestion that was never asked for
// shows, which is nothing -- #67's third acceptance test in one line, and the same
// promise CategorizerClient makes on the server. Nobody typing a transaction can
// act on "the categorizer is unreachable", and a screen that says so is a screen
// that reports somebody else's outage in the middle of a form.
//
// There is no `asking` either, and that is the less obvious call. A "thinking..."
// line would flash for four milliseconds against the rules (#61 -- what is
// deployed), and against a categorizer that is not there it would sit on screen
// for the whole timeout and then vanish -- an indicator asking the reader to wait
// for something they are never going to be given. What it would buy is feedback
// during the model's ~2 s, which is real, and is worth less than the two failures
// it introduces.
export type SuggestionState =
  | { status: 'none' }
  | { status: 'suggested'; category: string; source: string | null }
  | { status: 'unknown'; source: string }

// One object rather than a fresh `{ status: 'none' }` at each of the three places
// this is needed, so React's bail-out on an unchanged state can see it: a run of
// failed requests sets the same reference every time and re-renders nothing, where
// a new literal would re-render on each one to say the same thing.
const NONE: SuggestionState = { status: 'none' }

/**
 * Asks what category the description being typed would get, once the typing stops.
 */
// The whole of #67's first trap lives in this effect, and the machinery for it was
// already there. Three keystrokes make three requests and the second can answer
// after the third; the fix is to abort the superseded one, which the cleanup below
// does because React runs it before the effect runs again. `request` composes that
// signal with its own timeout through AbortSignal.any, and rethrows a caller's
// abort untouched so it can be recognised here.
//
// StrictMode is the free test of exactly that: in development React runs every
// effect twice on mount, so a missing cleanup doubles every request. Here the
// first run's timer is cleared before it can fire, and the doubled effect costs
// nothing at all rather than one wasted call.
//
// The dependencies are the three values rather than an object built from them,
// which is what makes "no request for a description that has not changed" true
// without anything comparing anything: an object literal would be a new reference
// on every render and the effect would run after every keystroke anywhere in the
// form.
export function useCategorySuggestion(
  description: string,
  amount: string,
  currency: string,
): SuggestionState {
  const [state, setState] = useState<SuggestionState>(NONE)

  // Whether there is anything worth asking about *now*, decided while rendering
  // rather than by clearing state from inside the effect. Both work; this one is
  // one render cheaper and it makes "an empty description shows nothing" a
  // property of what is drawn instead of something that is true once an effect has
  // caught up. `askable` is pure and is called a second time inside the effect,
  // which is the price: the alternative is passing the object it builds as a
  // dependency, and an object literal is a new reference on every render, so the
  // effect would fire after every keystroke anywhere in the form.
  const worthAsking = askable(description, amount, currency) !== null

  useEffect(() => {
    const query = askable(description, amount, currency)

    if (query === null) {
      return
    }

    const controller = new AbortController()

    const timer = setTimeout(() => {
      suggestCategory(query, controller.signal)
        .then((answer) => {
          // Aborted between the response arriving and this running. The state
          // belongs to a newer request by now, and writing here would put an
          // answer about "Lid" on top of the answer about "Lidl" -- which is the
          // out-of-order response #67 is about, arriving through the one gap the
          // abort itself does not close.
          if (controller.signal.aborted) {
            return
          }

          setState(toState(answer))
        })
        .catch(() => {
          if (controller.signal.aborted) {
            return
          }

          // Every failure, including the timeout, and it is deliberately not
          // distinguished. See SuggestionState: there is nothing here for the
          // person typing to do. Clearing rather than keeping what was there
          // matters -- the previous suggestion was about a previous description.
          setState(NONE)
        })
    }, DEBOUNCE_MS)

    return () => {
      clearTimeout(timer)
      controller.abort()
    }
  }, [description, amount, currency])

  // What that costs, and it is the one place this shows something it should not:
  // a description emptied and then typed again renders the *previous* answer until
  // the new one lands, because the state survives while nothing is worth asking.
  // It is the same staleness as keeping a suggestion visible during the 400 ms
  // debounce, which is deliberate -- see the note where this is rendered -- and it
  // is bounded by the same request. Nothing is stored from here, so the worst case
  // is a word on a screen that is briefly about the wrong description.
  return worthAsking ? state : NONE
}

/** The request to make, or null when there is nothing worth asking. */
// The rules are the server's rules, checked here to save a round trip that would
// certainly be refused -- not to replace them. The three that matter are the three
// `CategorySuggestionRequest` carries, and the amount is the one that is easy to
// get wrong in this direction: an empty field is `Number('') === 0`, which is a
// number, and which the categorizer refuses because `Field(gt=0)` on the Python
// side says a purchase of nothing is not a purchase.
//
// The description is sent exactly as typed, and only the trimmed length is
// measured. Trimming what is sent would be this client quietly showing a
// suggestion for a string the save is not going to use -- the save sends what is
// in the field, so this has to as well, or the two calls are about two different
// descriptions.
function askable(
  description: string,
  amount: string,
  currency: string,
): CategorySuggestionQuery | null {
  if (description.trim().length < MIN_DESCRIPTION_LENGTH) {
    return null
  }

  // Digits, optionally two decimal places, and nothing else. `Number` alone would
  // accept "12.345" and " 12 " and "1e3", each of which is a 400 from the server
  // and a request that was never worth making. The form's own step="0.01" refuses
  // the third decimal on submit and not while typing.
  if (!/^\d+(\.\d{1,2})?$/.test(amount.trim())) {
    return null
  }

  const value = Number(amount)

  if (value < 0.01) {
    return null
  }

  if (!/^[A-Za-z]{3}$/.test(currency)) {
    return null
  }

  return { amount: value, currency, description }
}

function toState(answer: {
  category: string | null
  source: string | null
}): SuggestionState {
  if (answer.category !== null) {
    return { status: 'suggested', category: answer.category, source: answer.source }
  }

  // No category, and the source is what separates the two reasons for that. A
  // named source declined; no source means nothing answered, and #67 asks for that
  // to be invisible rather than reported.
  return answer.source === null ? NONE : { status: 'unknown', source: answer.source }
}
