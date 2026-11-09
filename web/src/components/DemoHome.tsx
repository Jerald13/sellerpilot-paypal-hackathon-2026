import { useEffect, useState } from 'react'
import { api, type CurrentUser, type DemoInfo } from '../api'

interface Props {
  /** Called after the one-click seller sign-in; the back office then renders in place. */
  onSellerSignedIn: (user: CurrentUser) => void
}

/** Landing page of the hosted public demo: one link, both sides of the app, no password. */
export function DemoHome({ onSellerSignedIn }: Props) {
  const [demo, setDemo] = useState<DemoInfo | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.demoInfo().then(setDemo).catch((e: Error) => setError(e.message))
  }, [])

  const seller = demo?.accounts.find((a) => a.role === 'seller')
  const customers = demo?.accounts.filter((a) => a.role === 'customer') ?? []

  const signIn = async (account: CurrentUser) => {
    setBusy(account.email)
    setError(null)
    try {
      const user = await api.demoLogin(account.email)
      if (user.role === 'seller') onSellerSignedIn(user)
      else window.location.assign('/shop')
    } catch (e) {
      setError((e as Error).message)
      setBusy(null)
    }
  }

  return (
    <main className="demo-home">
      <div className="demo-hero">
        <span className="badge badge-ai">Live demo</span>
        <h2>Try SellerPilot</h2>
        <p className="muted">
          An AI assistant that handles PayPal admin for a small online shop. It looks things up and proposes refunds and reminders, but it never moves money:
          the shop owner approves every action. Everything runs on the PayPal sandbox, so no real money moves.
        </p>
      </div>

      <div className="demo-roles">
        <section className="panel demo-role">
          <div className="demo-role-head">
            <span className="demo-role-icon" aria-hidden>
              💼
            </span>
            <div>
              <h3>Shop owner</h3>
              <div className="muted small">The back office</div>
            </div>
          </div>
          <p className="small">Chat with the AI assistant about PayPal orders, invoices and refunds, approve or reject what it proposes, check the books against PayPal, and read the audit log.</p>
          <button
            type="button"
            className="button primary demo-go"
            disabled={!seller || busy !== null}
            onClick={() => seller && void signIn(seller)}
          >
            {busy === seller?.email ? 'Signing in…' : `Sign in as ${seller?.name ?? 'the seller'} →`}
          </button>
        </section>

        <section className="panel demo-role">
          <div className="demo-role-head">
            <span className="demo-role-icon" aria-hidden>
              🛍️
            </span>
            <div>
              <h3>Customer</h3>
              <div className="muted small">The storefront</div>
            </div>
          </div>
          <p className="small">See your orders and chat with the shop's AI assistant. It can explain an order and request a refund within the shop's policy, which the owner then approves.</p>
          <div className="demo-customers">
            {customers.map((c) => (
              <button key={c.email} type="button" className="button outline" disabled={busy !== null} onClick={() => void signIn(c)}>
                {busy === c.email ? 'Signing in…' : `${c.name} →`}
              </button>
            ))}
          </div>
        </section>
      </div>

      {error && <div className="error-text">{error}</div>}

      <section className="demo-explainer" aria-label="How SellerPilot works, animated">
        <iframe src="/how-it-works.html?embed=1" title="How SellerPilot works: an animated walk-through of a refund" loading="lazy" />
      </section>

      <section className="panel demo-steps">
        <h3>A 3-minute tour</h3>
        <ol>
          <li>
            As <b>John Doe</b>, ask the assistant: <i>“My cushion covers arrived torn. Can I get a refund for order SP-DVAN-1013?”</i> and request the refund it offers.
          </li>
          <li>
            Sign out, sign in as the <b>shop owner</b>, and approve John's request in <b>Approvals</b>. Only then does PayPal get called.
          </li>
          <li>
            Ask the assistant <i>“Which invoices are still unpaid?”</i>, then <i>“Send a polite reminder to all of them.”</i>
          </li>
          <li>
            Open <b>Reconciliation</b> and click <b>Run reconciliation</b>: it finds four planted mismatches between the books and PayPal.
          </li>
        </ol>
        <p className="muted small">
          You can be one person at a time in a browser. To be the owner and a customer side by side, open the other one in a private window. The demo data resets
          when the server restarts.
        </p>
      </section>
    </main>
  )
}
