import { useEffect, useRef, useState } from 'react'
import { getMe, logout, type Me } from './api/auth'
import {
  createTransaction,
  listCategories,
  listTransactions,
  updateCategory,
} from './api/transactions'
import type { NewTransaction } from './api/types'
import { BackfillCategories } from './components/BackfillCategories'
import { ExportLabelled } from './components/ExportLabelled'
import { ImportForm } from './components/ImportForm'
import { LoginForm } from './components/LoginForm'
import { MonthSummary } from './components/MonthSummary'
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

/** How long to wait before asking again whether a category has arrived. #92. */
// Two seconds against a sweep that runs every five: often enough that the category
// never feels like it needed a reload, rare enough that the polling is a handful of
// requests rather than a stream. Deliberately not equal to the sweep's interval --
// the two clocks are unsynchronised, so matching them would make the average wait
// one and a half sweeps instead of one.
const POLL_INTERVAL_MS = 2_000

/** How many polls to make with nothing to show for them before giving up. */
// The bound that stops this being a request every two seconds for ever, and it is
// needed rather than tidy: rows stay pending while the categorizer is down, and
// `Categorizer:SweepIntervalSeconds` of 0 turns categorising after the fact off
// altogether, which is a supported state. Thirty is a minute of waiting, which
// comfortably covers the app's 23-second cold start (#35) plus the categorizer's
// own.
//
// **Progress refills it**, which is what makes one number serve both a single save
// and a three-hundred-row import: as long as the count of pending rows is going
// down, something is working and there is a reason to keep asking. Only a minute of
// no change at all stops it, and a reload starts it again.
const MAX_IDLE_POLLS = 30

function App() {
  const [list, setList] = useState<ListState>({ status: 'loading' })
  const [session, setSession] = useState<SessionState>({ status: 'loading' })

  // #63. The eleven, fetched from the server so this client holds no copy of them.
  //
  // No loading or failed state of its own, unlike the two above, and the asymmetry
  // is deliberate. There is nothing useful to say about this request while it is in
  // flight -- the list is what the page is for and it is arriving at the same time
  // -- and there is nothing useful to say if it fails either, because the answer to
  // "the categories could not be fetched" is the same as the answer to "there are
  // no categories": show the stored category and offer no way to change it.
  // CategoryCell does exactly that with an empty array, so the empty state and the
  // failure state want the same rendering and do not need to be distinguishable.
  const [categories, setCategories] = useState<readonly string[]>([])

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

  // #92. What is left of the polling budget, and how many rows were waiting last
  // time it was looked at.
  //
  // Refs rather than state, on purpose: neither is rendered, and putting them in
  // state would re-render the table on every tick of a counter nobody can see. They
  // are read and written inside the effect below, which is where React allows it.
  const pollsLeft = useRef(MAX_IDLE_POLLS)
  const pendingLastSeen = useRef<number | null>(null)

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

  // #63. Its own effect rather than a second promise inside the one above, and not
  // because the two could not be awaited together -- they could. It is that this
  // one must not be a dependency of `reloads`: the list is fetched again after
  // every create, import and correction, and the eleven categories have not changed
  // in any of those cases. Folding it in would turn one request per page load into
  // one per write.
  useEffect(() => {
    if (session.status !== 'signedIn') {
      return
    }

    const controller = new AbortController()

    listCategories(controller.signal)
      .then(setCategories)

      // Swallowed, and this is the one place in this file where that is the right
      // thing to do. A failure here means the dropdown is not offered; the rows,
      // the amounts and the form all still work, and there is no action for the
      // reader to take. Reporting it in the list's own error state would replace a
      // working table with a message about a feature.
      .catch(() => setCategories([]))

    return () => controller.abort()
  }, [session.status])

  // #92. The category arrives after the row does, so something has to go and look.
  //
  // **The whole of the change on this side is that a save no longer waits.** The
  // server used to ask the categorizer before writing the row, so the 201 carried
  // the answer and the list fetched after it was already final. Now the row is
  // written immediately and a sweep fills the category in a few seconds later,
  // which is invisible to a client that asks once.
  //
  // Polling rather than server-sent events, which is the other honest answer and
  // needs an endpoint, a long-lived connection and a story about an application
  // that scales to zero. This costs a `setTimeout` and stops on its own.
  //
  // **It is self-limiting in the way that matters: an account whose rows all have
  // categories polls zero times.** The condition is not "recently saved" but "is
  // anything on screen still waiting", so nothing here has to know which action
  // caused the wait -- a create, an import, or a page opened while a previous
  // session's sweep was still running all behave the same.
  //
  // `list` is the dependency rather than a counter, so each answer schedules the
  // next question. That is a loop, and the two conditions below are what end it:
  // nothing pending, or a budget that progress has stopped refilling.
  useEffect(() => {
    if (session.status !== 'signedIn' || list.status !== 'ready') {
      return
    }

    const pending = list.transactions.filter((transaction) => transaction.categoryPending).length

    // Fewer waiting than last time means the sweep is working, so the budget is
    // worth spending again. The null case is the first look, which is not progress
    // but is the right moment to start from a full budget.
    if (pendingLastSeen.current === null || pending < pendingLastSeen.current) {
      pollsLeft.current = MAX_IDLE_POLLS
    }

    pendingLastSeen.current = pending

    if (pending === 0 || pollsLeft.current <= 0) {
      return
    }

    const controller = new AbortController()

    const timer = setTimeout(() => {
      pollsLeft.current -= 1

      listTransactions(controller.signal)
        .then((transactions) => setList({ status: 'ready', transactions }))

        // Swallowed, and this is the second place in this file where that is right.
        // Nobody asked for this request: it is the application checking on itself,
        // so a failure means the answer is not here yet, not that the screen the
        // reader is looking at has stopped working. Replacing a correct table with
        // "Could not reach the API" because a background poll failed would report a
        // problem the reader does not have. A session that has actually ended still
        // surfaces, on the next thing they do.
        .catch(() => {})
    }, POLL_INTERVAL_MS)

    return () => {
      clearTimeout(timer)
      controller.abort()
    }
  }, [list, session.status])

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
    //
    // #92: the 201's body is thrown away here as it always was, and it now carries
    // no category at all -- the sweep has not run yet. Which makes this reload the
    // moment the new row first appears, uncategorised and marked as waiting, and
    // the poll above takes it from there.
    reload()
  }

  // #63, and it is deliberately not `reload()`.
  //
  // handleCreate above asks the server for the whole list again and argues for it:
  // the order is (OccurredAt desc, CreatedAt desc), decided on the server, and a
  // back-dated entry belongs in the middle of the list rather than at the top --
  // so inserting client-side would mean writing that comparator a second time in
  // another language. None of that applies here. A correction changes neither sort
  // key, so the row cannot move, and the response carries the stored row: replacing
  // it in place is not a guess about where the server would have put it.
  //
  // Which is what answers the trap the issue names -- the list drops to "Loading..."
  // after a write, and a correction is a far worse place for that flicker than a
  // create. Not because it is uglier, but because a create is followed by an empty
  // form and a correction is followed by looking at the row to see whether it took.
  // Blanking the table at that moment answers the question with a spinner.
  //
  // Nothing is caught here: CategoryCell needs the rejection to decide what its own
  // select shows and what to say underneath it, and catching it here would leave
  // the cell with a promise that resolved after a write that did not happen.
  async function handleChangeCategory(id: string, category: string | null) {
    const updated = await updateCategory(id, category)

    setList((current) =>
      // The status is checked rather than assumed. The list can have gone back to
      // 'loading' while this request was in flight -- a reload started by the form
      // or the import -- and writing rows into that state would put a stale table
      // back on the screen underneath a newer request that is still arriving.
      current.status === 'ready'
        ? {
            status: 'ready',
            transactions: current.transactions.map((transaction) =>
              transaction.id === updated.id ? updated : transaction,
            ),
          }
        : current,
    )
  }

  async function handleSignOut() {
    await logout()

    // #92. A spent polling budget belongs to the session that spent it. Without
    // this, signing in as somebody else lands on a table that has already given up
    // asking, and their newest row would sit uncategorised until they reloaded.
    pollsLeft.current = MAX_IDLE_POLLS
    pendingLastSeen.current = null

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

          {/*
            #89. Under the import and above the summary, which puts the two CSV
            cards next to each other -- the one that reads four columns and the one
            that writes five. They are adjacent so that the difference is on the
            screen at the same time rather than remembered; they are two cards
            rather than one for the same reason.

            No callback, unlike ImportForm: an export changes nothing, so there is
            no list to fetch again. It is the only block on this page that reads
            and does not write.
          */}
          <ExportLabelled />

          {/*
            #93. Under the two CSV cards and above the summary, which is the order
            the work happens in: import rows, then ask about the ones that arrived
            with no category. It renders nothing when there is nothing to ask about,
            so the ordinary screen -- everything categorised -- is unchanged by it.

            It is given the same array the list is about to draw rather than a fetch
            of its own, which is #68's argument arriving at a second component: the
            count on the button and the blanks in the table below it cannot disagree,
            because they are the same rows counted twice.

            `reload` rather than a handler of its own, for the reason ImportForm gets
            it: the queued rows have to come back carrying `categoryPending`, and the
            poll above starts on its own once anything on screen is waiting.
          */}
          {list.status === 'ready' && (
            <BackfillCategories transactions={list.transactions} onBackfilled={reload} />
          )}

          {/*
            #68. Between the import and the list, and rendered off the very array
            the list is about to draw rather than off a fetch of its own -- which
            is what makes the totals and the rows below them incapable of
            disagreeing. It is also why there is nothing to show while the list is
            loading or has failed: the component underneath already says both of
            those things, once.

            The second half of the condition is about a screen rather than about
            the data. An empty *month* is a real state and MonthSummary renders it
            -- somebody who has spent nothing since the 1st should see that said
            rather than see nothing. An empty *account* is not a month problem, and
            without this it would stack "Nothing recorded this month." on top of
            the list's "Nothing recorded yet", which is the same fact twice and
            only one of them tells the reader what to do about it.
          */}
          {list.status === 'ready' && list.transactions.length > 0 && (
            <MonthSummary transactions={list.transactions} />
          )}

          <TransactionList
            state={list}
            onRetry={reload}
            categories={categories}
            onChangeCategory={handleChangeCategory}
          />
        </>
      )}
    </main>
  )
}

export default App
