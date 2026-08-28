import { useRef, useState } from 'react'
import { ApiError, importTransactions } from '../api/transactions'
import { IMPORT_REJECTED, IMPORT_SKIPPED, type ImportResult } from '../api/types'

interface ImportFormProps {
  /** Called after an import that stored at least one row, so the list is refetched. */
  onImported: () => void
}

/** What the form knows at a given moment. */
// The same union shape TransactionList uses, and for the same reason: "sending"
// and "failed" cannot both be true, and the compiler is what checks that rather
// than a reader.
//
// 'done' carries the whole result rather than a summary sentence, because the
// per-row report is the substance of #62 -- "an import that silently drops four
// lines is worse than one that refuses".
type ImportState =
  | { status: 'idle' }
  | { status: 'sending' }
  | { status: 'failed'; message: string }
  | { status: 'done'; result: ImportResult }

export function ImportForm({ onImported }: ImportFormProps) {
  const [state, setState] = useState<ImportState>({ status: 'idle' })
  const [file, setFile] = useState<File | null>(null)

  // A ref only so the chosen file can be cleared after a successful import. A
  // file input is one of the few genuinely uncontrolled elements in React -- its
  // value cannot be set from state for security reasons, and the empty string is
  // the one assignment a browser allows.
  const inputRef = useRef<HTMLInputElement>(null)

  async function handleSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()

    if (!file) {
      return
    }

    setState({ status: 'sending' })

    try {
      const result = await importTransactions(file)

      setState({ status: 'done', result })
      setFile(null)

      if (inputRef.current) {
        inputRef.current.value = ''
      }

      // Only when something was actually stored. A file that was entirely
      // duplicates changes nothing, and refetching the list would drop it to
      // "Loading..." to show the same rows back -- see the long note on
      // handleCreate in App.tsx for why that flicker is the cost of refetching
      // rather than inserting client-side.
      if (result.imported > 0) {
        onImported()
      }
    } catch (error: unknown) {
      setState({
        status: 'failed',
        message:
          error instanceof ApiError
            ? error.message
            : 'Could not import the file.',
      })
    }
  }

  const sending = state.status === 'sending'

  return (
    <section className="entry import" aria-labelledby="import-heading">
      <h2 id="import-heading">Import a CSV file</h2>

      <form onSubmit={handleSubmit}>
        <div className="field field-wide">
          <label htmlFor="import-file">File</label>

          {/*
            accept is a hint to the file picker and nothing more -- it filters
            what is offered and does not stop anyone choosing something else, so
            the server's 415 and its header check are still the rule. text/csv is
            listed beside the extension because a Windows machine with Excel
            installed reports a .csv file as application/vnd.ms-excel, and an
            accept list of only "text/csv" then hides the very files this is for.
          */}
          <input
            ref={inputRef}
            id="import-file"
            name="file"
            type="file"
            accept=".csv,text/csv"
            disabled={sending}
            onChange={(event) => {
              setFile(event.target.files?.[0] ?? null)

              // The previous report is about the previous file. Leaving it on
              // screen beside a newly chosen one invites reading it as this
              // file's result.
              setState({ status: 'idle' })
            }}
          />

          <p className="field-hint">
            Four columns, header first:{' '}
            <code>occurred_at,amount,currency,description</code>. Dates as
            2026-08-19, amounts as 1234.56 with a full stop and no thousands
            separator, saved as CSV UTF-8. A <code>category</code> column is
            read and ignored.
          </p>
        </div>

        {/*
          Disabled with no file chosen, so the one thing that can go wrong before
          a request is made cannot go wrong. The server would answer a 400 about
          an empty file, which is a correct sentence about a mistake the form
          could simply have prevented.
        */}
        <button type="submit" disabled={sending || !file}>
          {sending ? 'Importing...' : 'Import'}
        </button>
      </form>

      {state.status === 'failed' && (
        <div className="banner banner-error" role="alert">
          <p>{state.message}</p>
        </div>
      )}

      {state.status === 'done' && <ImportReport result={state.result} />}
    </section>
  )
}

/** What the server made of the file, row by row. */
function ImportReport({ result }: { result: ImportResult }) {
  // role="status" rather than "alert": a finished import is information, and an
  // assertive announcement would interrupt whatever a screen reader was saying.
  // The failure banner above is the one that interrupts.
  return (
    <div className="import-report" role="status">
      <p>
        <strong>
          {result.imported} of {result.rows} rows imported.
        </strong>{' '}
        {result.skipped > 0 && `${result.skipped} already recorded. `}
        {result.rejected > 0 && `${result.rejected} refused.`}
      </p>

      {result.imported > 0 && (
        // Said here rather than left for someone to notice. The import does not
        // call the categorizer -- one HTTP call per row would make a 300-row file
        // a request that runs for minutes -- so every row it stores arrives with
        // no category, and a screen that did not mention it would leave the empty
        // Category column looking like a bug.
        <p className="field-hint">
          Imported rows have no category: the categorizer is not called during an
          import.
        </p>
      )}

      {result.ignoredColumns.length > 0 && (
        <p className="field-hint">
          Columns read and ignored: {result.ignoredColumns.join(', ')}.
        </p>
      )}

      {result.problems.length > 0 && (
        <ul className="import-problems">
          {result.problems.map((problem) => (
            <li key={problem.lineNumber} data-outcome={problem.outcome}>
              <span className="import-line">Line {problem.lineNumber}</span>{' '}
              <span className="tag">
                {problem.outcome === IMPORT_SKIPPED
                  ? 'skipped'
                  : problem.outcome === IMPORT_REJECTED
                    ? 'refused'
                    : problem.outcome}
              </span>{' '}
              {problem.reason}
            </li>
          ))}
        </ul>
      )}

      {result.problemsTruncated && (
        <p className="field-hint">
          Only the first {result.problems.length} are listed. The counts above
          are complete.
        </p>
      )}
    </div>
  )
}
