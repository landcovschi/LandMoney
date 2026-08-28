import { useState, type FormEvent } from 'react'
import { login, register, type Me } from '../api/auth'
import { ApiError } from '../api/http'
import type { FieldErrors } from '../api/types'
import { FieldMessages } from './FieldMessages'

// The fields this form has somewhere to put a message. Anything the server files
// under another key is shown at the top instead of being dropped -- the same rule
// TransactionForm follows, and for the same reason: a 400 that produces no visible
// message is indistinguishable from a button that did nothing.
const OWN_FIELDS = new Set(['userName', 'password', 'inviteCode'])

interface LoginFormProps {
  /** Called once somebody is signed in, with whoever that turned out to be. */
  onSignedIn: (me: Me) => void
}

/** The screen a signed-out visitor gets, instead of the application. */
// One component for both signing in and signing up, switched by a link. Two
// components was the alternative: it reads more cleanly and it duplicates the
// username field, the password field, the error plumbing and the submitting state,
// which is most of the file. The only difference between the two modes is one
// extra input and which function is called.
export function LoginForm({ onSignedIn }: LoginFormProps) {
  const [registering, setRegistering] = useState(false)
  const [userName, setUserName] = useState('')
  const [password, setPassword] = useState('')
  const [inviteCode, setInviteCode] = useState('')

  const [submitting, setSubmitting] = useState(false)
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()

    setSubmitting(true)
    setFieldErrors({})
    setFormError(null)

    try {
      const me = registering
        ? await register(userName, password, inviteCode)
        : await login(userName, password)

      // The password is not cleared, and nothing else is either: this component
      // is about to be unmounted, because App swaps it for the application the
      // moment the session state changes. Clearing state on the way out is work
      // whose only effect would be a frame nobody sees.
      onSignedIn(me)
    } catch (error) {
      if (error instanceof ApiError) {
        setFieldErrors(error.fieldErrors)

        const hasFieldMessages = Object.keys(error.fieldErrors).length > 0
        setFormError(hasFieldMessages ? null : error.message)
      } else {
        setFormError('Something went wrong while signing in.')
      }

      // Only on failure, and only here. Leaving a rejected password in the box is
      // what lets somebody see they typed it into the username field, and
      // clearing it costs a second attempt from scratch.
      setSubmitting(false)
    }
  }

  const unattached = Object.entries(fieldErrors)
    .filter(([field]) => !OWN_FIELDS.has(field))
    .flatMap(([, messages]) => messages ?? [])

  const bannerMessages = formError ? [formError] : unattached

  function switchMode() {
    setRegistering((wasRegistering) => !wasRegistering)

    // The messages belong to the mode that produced them. "That username and
    // password do not match" sitting above a registration form is advice about a
    // request that was never made.
    setFieldErrors({})
    setFormError(null)
  }

  return (
    <form className="entry" onSubmit={handleSubmit}>
      <h2>{registering ? 'Create an account' : 'Sign in'}</h2>

      {bannerMessages.length > 0 && (
        // role="alert" is what makes a screen reader announce this the moment it
        // appears. Without it the message is on screen and silent, which for
        // someone not looking at that part of the page is the same as no message.
        <p className="banner banner-error" role="alert">
          {bannerMessages.join(' ')}
        </p>
      )}

      <div className="fields">
        <div className="field field-wide">
          <label htmlFor="userName">Username</label>
          <input
            id="userName"
            name="userName"
            type="text"
            required
            // The browser's own credential manager reads these. Without them it
            // offers to save nothing, and the next visit is typed by hand -- which
            // for an application used weekly is the difference between a password
            // that is long and one that is memorable.
            autoComplete="username"
            autoFocus
            value={userName}
            onChange={(event) => setUserName(event.target.value)}
            aria-invalid={fieldErrors.userName ? true : undefined}
            aria-describedby={fieldErrors.userName ? 'userName-error' : undefined}
          />
          <FieldMessages id="userName-error" messages={fieldErrors.userName} />
        </div>

        <div className="field field-wide">
          <label htmlFor="password">Password</label>
          <input
            id="password"
            name="password"
            type="password"
            required
            // current-password when signing in, new-password when registering, and
            // the difference is not cosmetic: new-password is what makes a password
            // manager offer to generate one, and current-password is what stops it
            // offering to change the saved entry on every sign-in.
            autoComplete={registering ? 'new-password' : 'current-password'}
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            aria-invalid={fieldErrors.password ? true : undefined}
            aria-describedby={fieldErrors.password ? 'password-error' : undefined}
          />
          <FieldMessages id="password-error" messages={fieldErrors.password} />

          {registering && (
            // The rule, said before it is broken rather than after. The number is
            // written here and on IdentityOptions.Password.RequiredLength, which is
            // the two-places problem #6's validation rule exists to avoid -- taken
            // knowingly, because a password rule discovered by failing is a worse
            // trade than a number that has to change in two files.
            <p className="field-hint">At least 10 characters.</p>
          )}
        </div>

        {registering && (
          <div className="field field-wide">
            <label htmlFor="inviteCode">Invite code</label>
            <input
              id="inviteCode"
              name="inviteCode"
              type="text"
              // Not `required`. On a developer machine with no code configured the
              // server asks for none, and a client cannot tell which kind of server
              // it is talking to. Marking it required here would make the local
              // loop demand a value that means nothing.
              autoComplete="off"
              value={inviteCode}
              onChange={(event) => setInviteCode(event.target.value)}
              aria-invalid={fieldErrors.inviteCode ? true : undefined}
              aria-describedby={
                fieldErrors.inviteCode ? 'inviteCode-error' : undefined
              }
            />
            <FieldMessages
              id="inviteCode-error"
              messages={fieldErrors.inviteCode}
            />
          </div>
        )}
      </div>

      <button type="submit" disabled={submitting} aria-busy={submitting}>
        {submitting
          ? registering
            ? 'Creating...'
            : 'Signing in...'
          : registering
            ? 'Create account'
            : 'Sign in'}
      </button>

      {/*
        A button, not a link. It changes what is on the screen and navigates
        nowhere, and an <a href="#"> that does that is a thing a screen reader
        announces as a link to the top of the page.
      */}
      <p className="session">
        <button type="button" className="link" onClick={switchMode}>
          {registering
            ? 'I already have an account'
            : 'I have an invite code and need an account'}
        </button>
      </p>
    </form>
  )
}
