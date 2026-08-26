import { useEffect, useState } from 'react'
import { createTransaction, getMe, listTransactions, type Me } from './api/transactions'
import type { NewTransaction } from './api/types'
import { TransactionForm } from './components/TransactionForm'
import { TransactionList, type ListState } from './components/TransactionList'
import './App.css'

/** Who is signed in, once the server has been asked. */
// A third state rather than `Me | null`, because "not asked yet" and "nobody is
// signed in" are different things to put on screen, and collapsing them makes the
// header flash a sign-in link at somebody who is signed in.
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

  // #52. Asked once, on mount, and never again: a session that ends while the
  // page is open surfaces as a 401 on the next list request instead, which is
  // where the user is already looking. Polling this would be a request every few
  // seconds against a container that scales to zero.
  //
  // getMe answers null rather than throwing on 401 -- "nobody is signed in" is
  // what the question was, so it is an answer. Anything else is a real failure and
  // is deliberately left to fall through to the list's own error reporting rather
  // than given a second place to be shown.
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
  }, [reloads])

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

  return (
    <main>
      <header>
        <h1>LandMoney</h1>
        <p>What has been spent, and when.</p>

        {/*
          Plain anchors, not fetch. Both of these are navigations by nature: one
          ends at the identity provider and comes back with a cookie, the other
          ends at the provider's sign-out and comes back. A fetch would follow the
          redirects itself and change nothing the user can see.

          Sign-out is a GET because it has to work as a link. The usual objection
          is that a third-party page could trigger it with an <img> tag; the cost
          of that is being signed out, which is the least valuable thing an
          attacker could do here, and the alternative is a form and an antiforgery
          token for a one-user application.
        */}
        {session.status === 'signedIn' && (
          <p className="session">
            Signed in as {session.me.name ?? 'someone'}.{' '}
            <a href="/auth/logout">Sign out</a>
          </p>
        )}

        {session.status === 'signedOut' && (
          <p className="session">
            <a href="/auth/login">Sign in</a>
          </p>
        )}
      </header>

      {/*
        The form is hidden while signed out, and the list is left to report its
        own 401. Hiding the form is not a security measure -- the API refuses an
        anonymous POST whatever this renders, which AuthorizationTests asserts --
        it is that offering a form whose submit cannot succeed is a worse screen
        than not offering one.
      */}
      {session.status !== 'signedOut' && <TransactionForm onSubmit={handleCreate} />}

      <TransactionList state={list} onRetry={reload} />
    </main>
  )
}

export default App
