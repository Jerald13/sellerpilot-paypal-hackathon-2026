import { useEffect, useState, type FormEvent } from 'react'
import { api, type CurrentUser, type DemoInfo } from '../api'

interface Props {
  title: string
  subtitle: string
  /** Demo accounts for this screen: one-click fills locally, one-click sign-in on the hosted public demo. */
  demoEmails: string[]
  onSignedIn: (user: CurrentUser) => void
}

export function LoginForm({ title, subtitle, demoEmails, onSignedIn }: Props) {
  const [email, setEmail] = useState(demoEmails[0] ?? '')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [demo, setDemo] = useState<DemoInfo | null>(null)

  useEffect(() => {
    api.demoInfo().then(setDemo).catch(() => undefined)
  }, [])

  const run = async (signIn: () => Promise<CurrentUser>) => {
    setBusy(true)
    setError(null)
    try {
      onSignedIn(await signIn())
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const submit = (event: FormEvent) => {
    event.preventDefault()
    void run(() => api.login(email, password))
  }

  const accounts = demoEmails.map((e) => demo?.accounts.find((a) => a.email === e) ?? { email: e, name: e, role: 'customer' as const })

  if (demo?.publicDemo) {
    return (
      <div className="panel login">
        <h2>{title}</h2>
        <p className="muted small">{subtitle}</p>
        <p className="demo-note">Public demo: pick an account, no password needed. Everything runs on the PayPal sandbox, so no real money moves.</p>
        {accounts.map((a) => (
          <button key={a.email} type="button" className="button primary" disabled={busy} onClick={() => void run(() => api.demoLogin(a.email))}>
            Sign in as {a.name}
            {a.role === 'seller' ? ' (seller)' : ''}
          </button>
        ))}
        {error && <div className="error-text">{error}</div>}
      </div>
    )
  }

  return (
    <form className="panel login" onSubmit={submit}>
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
      <p className="muted small">
        Demo password: the <code>Shop__DemoPassword</code> value you set in <code>.env</code> (see the README).
      </p>
    </form>
  )
}
