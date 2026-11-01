import { formatMoney, formatTime, type PendingAction } from '../api'

interface Props {
  action: PendingAction
  busy: boolean
  onApprove: (id: string) => void
  onReject: (id: string) => void
}

const statusLabel: Record<PendingAction['status'], string> = {
  Pending: 'Waiting for your approval',
  Approved: 'Approved, executing…',
  Rejected: 'Rejected',
  Executed: 'Done',
  Failed: 'Failed',
}

export function ActionCard({ action, busy, onApprove, onReject }: Props) {
  const isRefund = action.type === 'Refund'
  const details = action.details ?? {}
  const flags = details.securityFlags ?? []
  const pending = action.status === 'Pending'

  return (
    <article className={`card card-${action.status.toLowerCase()}`}>
      <header className="card-header">
        <span className={`badge ${isRefund ? 'badge-refund' : 'badge-reminder'}`}>{isRefund ? 'Refund' : 'Invoice reminder'}</span>
        <span className="card-amount">{formatMoney(action.amount, action.currency)}</span>
      </header>

      <p className="card-summary">{action.summary}</p>

      {flags.length > 0 && (
        <div className="warning" role="alert">
          <strong>Suspicious text in this order.</strong> SellerPilot withheld it from the AI:
          {flags.map((flag, i) => (
            <blockquote key={i}>
              “{flag.excerpt}” <span className="muted">({flag.rules.join(', ')})</span>
            </blockquote>
          ))}
        </div>
      )}

      {!isRefund && details.note && (
        <div className="message-preview">
          <div className="muted">Message that will be sent to the customer:</div>
          {details.subject && <div className="preview-subject">{details.subject}</div>}
          <div className="preview-note">{details.note}</div>
        </div>
      )}

      <p className="card-reason">
        <span className="muted">AI's reason:</span> {action.reason}
      </p>

      {pending ? (
        <div className="card-actions">
          <button className="button primary" disabled={busy} onClick={() => onApprove(action.id)}>
            {busy ? 'Working…' : isRefund ? `Approve refund` : 'Approve & send'}
          </button>
          <button className="button" disabled={busy} onClick={() => onReject(action.id)}>
            Reject
          </button>
        </div>
      ) : (
        <div className={`card-result result-${action.status.toLowerCase()}`}>
          <strong>{statusLabel[action.status]}</strong>
          {action.status === 'Executed' && isRefund && action.payPalResultId && <> · PayPal refund ID <code>{action.payPalResultId}</code></>}
          {action.status === 'Executed' && !isRefund && <> · reminder sent through PayPal</>}
          {action.error && <div className="error-text">{action.error}</div>}
          {action.decidedAt && <div className="muted small">{formatTime(action.decidedAt)}</div>}
        </div>
      )}
    </article>
  )
}
