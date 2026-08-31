import { useState } from 'react'
import { ApiError, exportLabelled } from '../api/transactions'
import type { LabelledExport } from '../api/transactions'

/** What the panel knows at a given moment. */
// The same union shape ImportForm and TransactionList use, so that "downloading"
// and "failed" cannot both be true and the compiler is what checks it.
//
// 'done' carries the whole result rather than a sentence, because the row count and
// the file name are two separate things the reader needs: one says whether the
// export was worth doing and the other says what to look for in the downloads
// folder.
type ExportState =
  | { status: 'idle' }
  | { status: 'exporting' }
  | { status: 'failed'; message: string }
  | { status: 'done'; result: LabelledExport }

/** #89. The rows corrected by hand, on their way to `evals/transactions.csv`. */
// A card of its own rather than a second control inside the import form, although
// the two are opposite operations on CSV and grouping them would read tidily. #89's
// third trap is that the two files have different shapes and the same kind of name,
// and one card holding both an upload and a download is where somebody eventually
// feeds one to the other. Two cards, each stating its own columns.
export function ExportLabelled() {
  const [state, setState] = useState<ExportState>({ status: 'idle' })

  async function handleExport() {
    setState({ status: 'exporting' })

    try {
      const result = await exportLabelled()

      // Nothing is downloaded when nothing was labelled, and it is a decision
      // rather than a guard against an empty string. The body is still a valid
      // file -- the header line and no rows -- and it is precisely the file that
      // does damage: appended to evals/transactions.csv it puts a second header in
      // the middle of the set, which score.py refuses as a row whose date is the
      // word `occurred_at`. Saying "there are none" is both the truer answer and
      // the one that cannot be pasted into the wrong place.
      if (result.rows > 0) {
        save(result)
      }

      setState({ status: 'done', result })
    } catch (error: unknown) {
      setState({
        status: 'failed',
        message:
          error instanceof ApiError
            ? error.message
            : 'Could not export the labelled rows.',
      })
    }
  }

  const exporting = state.status === 'exporting'

  return (
    <section className="entry export" aria-labelledby="export-heading">
      <h2 id="export-heading">Export labelled rows</h2>

      <p className="field-hint">
        Every transaction whose category you corrected yourself, as the five
        columns the eval set holds:{' '}
        <code>occurred_at,amount,currency,description,category</code>. Rows the
        categorizer guessed are left out on purpose -- scoring a predictor
        against its own past answers measures nothing.
      </p>

      <button
        type="button"
        onClick={handleExport}
        disabled={exporting}
        aria-busy={exporting}
      >
        {exporting ? 'Exporting...' : 'Export'}
      </button>

      {state.status === 'failed' && (
        <div className="banner banner-error" role="alert">
          <p>{state.message}</p>
        </div>
      )}

      {state.status === 'done' && <ExportReport result={state.result} />}
    </section>
  )
}

/** What came out, and what to do with it. */
function ExportReport({ result }: { result: LabelledExport }) {
  // role="status" rather than "alert", matching ImportReport: a finished export is
  // information, and the failure banner above is the one that interrupts.
  if (result.rows === 0) {
    return (
      <div className="import-report" role="status">
        <p>
          <strong>Nothing to export yet.</strong> A row is exported once you have
          set or corrected its category yourself -- the badge in the list reads{' '}
          <code>human</code>.
        </p>
      </div>
    )
  }

  return (
    <div className="import-report" role="status">
      <p>
        <strong>
          {result.rows} {result.rows === 1 ? 'row' : 'rows'} exported
        </strong>{' '}
        to <code>{result.fileName}</code>.
      </p>

      {/*
        The header line is the whole of what makes this more than "open it and
        copy". The file is a valid eval set on its own -- score.py --set reads it,
        which is worth knowing before merging it into the recorded one -- and that
        is exactly why it cannot simply be concatenated onto a file that already
        has a header.
      */}
      <p className="field-hint">
        It is an eval set on its own:{' '}
        <code>python evals/score.py --set {result.fileName}</code>. To add the
        rows to the recorded one, append it without its header line:{' '}
        <code>tail -n +2 {result.fileName} &gt;&gt; evals/transactions.csv</code>,
        then re-run the scorer and update <code>evals/baseline.json</code> in the
        same commit.
      </p>
    </div>
  )
}

/** Hands the file to the browser. */
// A Blob and a synthetic click rather than an <a href> pointing at the endpoint.
// The link is one line and gets the file name from Content-Disposition for free,
// and it loses the whole of the reporting above: a link navigates, so a 401 on a
// session that ended while the tab was open produces a blank tab rather than the
// sentence http.ts already knows how to write, and there is nowhere to put the row
// count or the two commands.
//
// revokeObjectURL is not tidying. An object URL pins its blob for the lifetime of
// the document, so on a page nobody reloads -- which is this one -- every export
// would keep its copy in memory until the tab closed.
function save({ csv, fileName }: LabelledExport) {
  // The type is written out rather than left to default. A Blob with no type is
  // served to the browser's own save dialog as application/octet-stream, which on
  // Windows is what makes a .csv arrive as "unknown file" in some browsers.
  const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv' }))

  const link = document.createElement('a')
  link.href = url
  link.download = fileName

  // Appended to the document before clicking: a detached anchor's click is ignored
  // by Firefox, and works in Chrome, which is the sort of difference that ships.
  document.body.appendChild(link)
  link.click()
  link.remove()

  // Revoked a turn later rather than on the next line. The click queues the
  // download; it does not perform it, and a URL revoked before the browser has
  // read the blob is a download that silently produces nothing -- which is
  // Safari's behaviour and has been Chrome's for large files.
  setTimeout(() => URL.revokeObjectURL(url), 0)
}
