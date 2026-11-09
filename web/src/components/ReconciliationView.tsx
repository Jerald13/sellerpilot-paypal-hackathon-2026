import { useEffect, useState } from 'react'
import { api, formatMoney, formatTime, type Finding, type ReconciliationRun } from '../api'

interface Props {
  /** Hands a finding to the assistant, which explains it and proposes a fix for approval. */
  onAskAssistant: (text: string) => void
}

const kindLabel: Record<string, string> = {
  PaymentNotCompleted: 'Payment not completed',
  PaymentMismatch: 'Payment amount differs',
  PaymentNotInBooks: 'Payment missing from books',
  RefundNotInBooks: 'Refund missing from books',
  RefundNotInPayPal: 'Refund missing at PayPal',
  Overcharged: 'Customer overcharged',
  Undercharged: 'Customer undercharged',
  PayPalOrderMissing: 'PayPal order missing',
  UnknownPayment: 'Payment without an order',
}

export function ReconciliationView({ onAskAssistant }: Props) {
  const [run, setRun] = useState<ReconciliationRun | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.latestReconciliation().then((r) => setRun(r ?? null)).catch((e: Error) => setError(e.message))
  }, [])

  const runNow = async () => {
    setBusy(true)
    setError(null)
    try {
      setRun(await api.runReconciliation())
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const ordersWithProblems = run ? run.ordersChecked - run.ordersMatched : 0
  const paymentsWithoutOrder = run ? run.findings.filter((f) => f.kind === 'UnknownPayment').length : 0

  const ask = (f: Finding) =>
    onAskAssistant(
      `Reconciliation found a problem${f.orderNumber ? ` with order ${f.orderNumber}` : ''}: ${f.message} What should I do? If it can be fixed here, propose the fix.`,
    )

  return (
    <section className="panel data-view">
      <div className="panel-title">
        <h2>Reconciliation: do the books match PayPal?</h2>
        <button className="button primary" onClick={() => void runNow()} disabled={busy}>
          {busy ? 'Checking every order at PayPal…' : 'Run reconciliation'}
        </button>
      </div>
      <p className="muted small">
        Compares the shop's records with live PayPal data: payments recorded vs captured, refunds recorded vs made, what customers paid vs order totals, and
        PayPal payments with no shop order. The rules are code; the AI only explains and proposes fixes, which you approve.
      </p>
      {error && <div className="error-text">{error}</div>}
      {!run && !busy && <div className="empty">No reconciliation yet. Click “Run reconciliation”.</div>}

      {run && (
        <>
          <div className="summary-tiles three">
            <div className="tile">
              <div className="tile-value">{run.ordersChecked}</div>
              <div className="muted small">shop orders paid via PayPal, checked one by one</div>
              <div className="small tile-breakdown">
                <span className="ok">{run.ordersMatched} match</span> · <span className={ordersWithProblems > 0 ? 'bad' : 'ok'}>{ordersWithProblems} with a problem</span>
              </div>
            </div>
            <div className="tile">
              <div className="tile-value">{run.payPalPaymentsChecked}</div>
              <div className="muted small">PayPal payments scanned for ones with no shop order</div>
              <div className="small tile-breakdown">
                <span className={paymentsWithoutOrder > 0 ? 'bad' : 'ok'}>{paymentsWithoutOrder} without an order</span>
              </div>
            </div>
            <div className="tile">
              <div className={`tile-value ${run.issuesFound > 0 ? 'bad' : 'ok'}`}>{run.issuesFound}</div>
              <div className="muted small">issues to fix in total (listed below)</div>
            </div>
          </div>
          <div className="muted small">
            Run #{run.id} · {formatTime(run.finishedAt ?? run.startedAt)} · started by {run.trigger}
            {run.note && <> · {run.note}</>}
          </div>
          {run.status === 'Failed' && <div className="error-text">{run.error}</div>}
          {run.status === 'Completed' && run.findings.length === 0 && <div className="empty">Everything matches. 🎉</div>}

          <div className="findings">
            {run.findings.map((f) => (
              <article key={f.id} className={`finding severity-${f.severity.toLowerCase()}`}>
                <header>
                  <span className={`badge sev-${f.severity.toLowerCase()}`}>{f.severity}</span>
                  <strong>{kindLabel[f.kind] ?? f.kind}</strong>
                  {f.orderNumber && <span className="muted">· {f.orderNumber}</span>}
                  {f.difference !== null && f.currency && <span className="finding-amount">{formatMoney(f.difference, f.currency)}</span>}
                </header>
                <p>{f.message}</p>
                {f.suggestedFix && (
                  <p className="muted small">
                    <strong>Suggested fix:</strong> {f.suggestedFix}
                  </p>
                )}
                <button className="button" onClick={() => ask(f)}>
                  Ask SellerPilot to fix →
                </button>
              </article>
            ))}
          </div>
        </>
      )}
    </section>
  )
}
