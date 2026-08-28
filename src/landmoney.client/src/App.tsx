import { useEffect, useState } from 'react'
import { getMe, logout, type Me } from './api/auth'
import { createTransaction, listTransactions } from './api/transactions'
import type { NewTransaction } from './api/types'
import { ImportForm } from './components/ImportForm'
import { LoginForm } from './components/LoginForm'
import { TransactionForm } from './components/TransactionForm'
import { TransactionList, type ListState } from './components/TransactionList'
import './App.css'

/** Who is signed in, once the server has been asked. */
// Three states rather than `Me | null`, because "not asked yet" and "nobody is
// signed in" are different things to put on screen. Collapsing them shows the
// login form for one frame to somebody who is already signed in, on every single
// page load -- which looks like being signed out and is the sort of flicker that
// makes an application feel broken.
type SessionState =
  | { status: 'loading' }
  | { status: 'signedIn'; me: Me }
  | { status: 'signedOut' }

function App() {
  const [list, setList] = useState<ListState>({ status: 'loading' })
  const [session, setSession] = useState<SessionState>({ status: 'loading' })

  // A counter, incremented to ask for the list again. It is a dependency of the
  // effect below, so changing it re-runs the effect -- which is the boring way
  // to say "do that again" to a useEffect.
  //
  // The alternative is a useCallback that fetches, called from the effect and
  // from both buttons. It reads better right up to the point where that
  // callback has to cancel the request the previous call started, which the
  // effect's own cleanup already does for nothing.
  const [reloads, setReloads] = useState(0)

  // The status is moved here rather than set at the top of the effect, where it
  // reads more naturally. Two reasons, and the linter only knows the first:
  // a setState inside an effect starts a second render before the browser has
  // painted the first, so the extra one is pure waste. And the effect does not
  // run on mount only to announce a state it was already in -- useState above
  // starts at 'loading'. This is the state belonging to the event that caused
  // it, which is where React wants it.
  const reload = () => {
    setList({ status: 'loading' })
    setReloads((count) => count + 1)
  }

  // #52. Asked once, on mount. A session that ends while the page is open
  // surfaces as a 401 from the list request instead, which is where the user is
  // already looking; polling this would be a request every few seconds against a
  // container that scales to zero.
  //
  // getMe answers null rather than throwing on 401 -- "nobody is signed in" is
  // what the question was, so it is an answer. Anything else is a real failure,
  // and it is treated as signed-out on purpose: the login form is the one screen
  // that is useful when the server cannot be reached, because it is also the
  // screen you need if the reason was that the session had gone.
  useEffect(() => {
    const controller = new AbortController()

    getMe(controller.signal)
      .then((me) => setSession(me ? { status: 'signedIn', me } : { status: 'signedOut' }))
      .catch(() => {
        if (!controller.signal.aborted) {
          setSession({ status: 'signedOut' })
        }
      })

    return () => controller.abort()
  }, [])

  useEffect(() => {
    // Nothing to list until there is somebody to list it for. Without this guard
    // the first render fires a request that is certain to be refused, and the
    // login screen would appear with "The session has ended" printed under it --
    // a true sentence about a session that never started.
    if (session.status !== 'signedIn') {
      return
    }

    // React runs every effect twice in development under StrictMode, on
    // purpose: it is hunting for exactly the effects that break when run twice.
    // Two requests go out, and without an abort the slower of the two wins and
    // writes its answer over the newer one.
    //
    // The C# parallel is passing a CancellationToken into an async method and
    // cancelling it when the caller goes away. It matters more here because a
    // component can unmount mid-request and React will not mention it to the
    // request.
    const controller = new AbortController()

    listTransactions(controller.signal)
      .then((transactions) => setList({ status: 'ready', transactions }))
      .catch((error: unknown) => {
        // Aborted means this effect was superseded or the component went away.
        // Nobody is left to read a message, and writing state here would land
        // on top of whatever the newer request has already put there.
        if (controller.signal.aborted) {
          return
        }

        setList({
          status: 'failed',
          message:
            error instanceof Error
              ? error.message
              : 'Could not load the transactions.',
        })
      })

    return () => controller.abort()
  }, [reloads, session.status])

  // Deliberately not caught here. The form needs the ApiError itself to put the
  // server's messages beside its own fields, and catching it in this function
  // would leave the form with a rejected promise it never sees.
  async function handleCreate(transaction: NewTransaction) {
    await createTransaction(transaction)

    // Asking the server for the list again rather than pushing the returned row
    // onto it -- and the 201's body is thrown away for it, knowingly. The order
    // is (OccurredAt desc, CreatedAt desc), decided in TransactionEndpoints, and
    // a back-dated entry belongs in the middle of the list rather than at the
    // top. Inserting client-side means writing that comparator a second time, in
    // another language, with nothing keeping the two in step. One extra round
    // trip is cheaper than a sort order that drifts.
    //
    // The visible cost is that the list drops to "Loading..." for the length of
    // that round trip instead of keeping the rows on screen. Holding the
    // previous list while a newer one is on the way is the fix, and it needs a
    // fourth state; not worth it while the round trip is a few milliseconds
    // against a local Postgres.
    //
    // The sharper cost, seen in review of #28 and left in on purpose: these are
    // two requests, and only the first one decides whether the transaction was
    // saved. If the create succeeds and the list request then fails -- the API
    // going down between the two, a timeout on a slow connection -- the form
    // clears, correctly, and the list says "Could not reach the API". The row is
    // in Postgres and the screen says the opposite.
    //
    // Rare, and every fix costs the fourth state this comment just argued
    // against: keeping the previous rows and reporting the refresh as a refresh
    // is the honest one. It is written down rather than fixed because the fix is
    // the same fix as the paragraph above, and both become worth it together --
    // not because nobody noticed.
    reload()
  }

  async function handleSignOut() {
    await logout()

    setSession({ status: 'signedOut' })

    // The rows go with the session. Leaving them in state would put one person's
    // spending on the screen behind the next person's login form -- which is the
    // failure this whole issue exists to prevent, reproduced in the client after
    // the server had got it right.
    setList({ status: 'loading' })
  }

  return (
    <main>
      <header>
        <h1>LandMoney</h1>
        <p>What has been spent, and when.</p>

        {session.status === 'signedIn' && (
          <p className="session">
            Signed in as {session.me.name ?? 'someone'}.{' '}
            {/*
              A button, not a link. Signing out is a POST -- a GET that ends a
              session can be triggered by any page that can make this browser
              fetch an image -- and an <a> that fires a request is a link that
              lies about what it does.
            */}
            <button type="button" className="link" onClick={handleSignOut}>
              Sign out
            </button>
          </p>
        )}
      </header>

      {/*
        Nothing is rendered until the session is known, which is the whole point
        of the third state above. `null` rather than a spinner: the answer arrives
        in a few milliseconds on a warm container, and a spinner that flashes is
        worse than a beat of nothing.

        Hiding the application is not the security measure -- the API refuses every
        anonymous request whatever this renders, which AuthorizationTests asserts.
        It is that a form whose submit cannot succeed is a worse screen than the
        one that says why.
      */}
      {session.status === 'loading' && null}

      {session.status === 'signedOut' && (
        <LoginForm onSignedIn={(me) => setSession({ status: 'signedIn', me })} />
      )}

      {session.status === 'signedIn' && (
        <>
          <TransactionForm onSubmit={handleCreate} />

          {/*
            Below the form and above the list, deliberately. Typing one
            transaction is the everyday act and stays at the top; importing a
            file is the occasional one. Putting it above the form would make the
            first thing on the screen the thing almost nobody is here to do.

            `reload` rather than a handler of its own: an import that stored rows
            wants exactly what a create wants, which is the list fetched again.
            The cost is the same one handleCreate's comment describes -- the list
            blinks through "Loading..." rather than holding the previous rows.
          */}
          <ImportForm onImported={reload} />

          <TransactionList state={list} onRetry={reload} />
        </>
      )}
    </main>
  )
}

export default App
