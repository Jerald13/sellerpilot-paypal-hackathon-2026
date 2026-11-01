import { useState, type FormEvent } from 'react'
import { api, type CurrentUser } from '../api'

interface Props {
  title: string
  subtitle: string
  /** Demo accounts shown as one-click fills (the password still has to be typed). */
  demoEmails: string[]
  onSignedIn: (user: CurrentUser) => void
}

export function LoginForm({ title, subtitle, demoEmails, onSignedIn }: Props) {
  const [email, setEmail] = useState(demoEmails[0] ?? '')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      onSignedIn(await api.login(email, password))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="panel login" onSubmit={(e) => void submit(e)}>
      <h2>{title}</h2>
      <p className="muted small">{subtitle}</p>
      <label>
        Email
        <input value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="username" required />
      </label>
      <label>
        Password
        <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" required />
      </label>
      {error && <div className="error-text">{error}</div>}
      <button className="button primary" type="submit" disabled={busy}>
        {busy ? 'Signing in…' : 'Sign in'}
      </button>
      {demoEmails.length > 1 && (
        <div className="demo-accounts">
          <span className="muted small">Demo accounts:</span>
          {demoEmails.map((d) => (
            <button key={d} type="button" className="chip" onClick={() => setEmail(d)}>
              {d}
            </button>
          ))}
        </div>
      )}
      <p className="muted small">Demo password: the <code>Shop__DemoPassword</code> value in your <code>.env</code>.</p>
    </form>
  )
}
