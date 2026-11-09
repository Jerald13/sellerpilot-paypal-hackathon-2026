import { formatMoney, formatTime, type PendingAction } from '../api'

interface Props {
  action: PendingAction
  busy: boolean
  onApprove: (id: string) => void
  onReject: (id: string) => void
  onReturnReceived: (id: string) => void
}

const statusLabel: Record<PendingAction['status'], string> = {
  Pending: 'Waiting for your approval',
  AwaitingReturn: 'Waiting for the customer to send the item back',
  Approved: 'Approved, executing…',
  Rejected: 'Rejected',
  Executed: 'Done',
  Failed: 'Failed',
}

const typeLabel: Record<PendingAction['type'], string> = {
  Refund: 'Refund',
  InvoiceReminder: 'Invoice reminder',
  BooksCorrection: 'Books correction',
}

const badgeClass: Record<PendingAction['type'], string> = {
  Refund: 'badge-refund',
  InvoiceReminder: 'badge-reminder',
  BooksCorrection: 'badge-books',
}

const approveLabel: Record<PendingAction['type'], string> = {
  Refund: 'Approve refund',
  InvoiceReminder: 'Approve & email',
  BooksCorrection: 'Approve correction',
}

export function ActionCard({ action, busy, onApprove, onReject, onReturnReceived }: Props) {
  const details = action.details ?? {}
  const flags = details.securityFlags ?? []
  const fromCustomer = action.requestedBy === 'customer'
  const open = action.status === 'Pending' || action.status === 'AwaitingReturn'

  return (
    <article className={`card card-${action.status.toLowerCase()}`}>
      <header className="card-header">
        <span className="row">
          <span className={`badge ${badgeClass[action.type]}`}>{typeLabel[action.type]}</span>
          {fromCustomer && <span className="badge badge-customer">Requested by customer</span>}
        </span>
        <span className="card-amount">{formatMoney(action.amount, action.currency)}</span>
      </header>

      <p className="card-summary">{action.summary}</p>

      {flags.length > 0 && (
        <div className="warning" role="alert">
          <strong>Suspicious text {fromCustomer ? 'in this request' : 'in this order'}.</strong> SellerPilot withheld it from the AI:
          {flags.map((flag, i) => (
            <blockquote key={i}>
              “{flag.excerpt}” <span className="muted">({flag.rules.join(', ')})</span>
            </blockquote>
          ))}
        </div>
      )}

      {action.status === 'AwaitingReturn' && (
        <div className="notice">
          The order total is above the return-first limit, so the customer must send the item back. Click <strong>Return received</strong> when it arrives;
          only then can the refund be approved.
        </div>
      )}

      {action.type === 'InvoiceReminder' && details.note && (
        <div className="message-preview">
          <div className="muted">Email that PayPal will send to the customer when you approve:</div>
          {details.subject && <div className="preview-subject">{details.subject}</div>}
          <div className="preview-note">{details.note}</div>
        </div>
      )}

      {action.type === 'BooksCorrection' && (
        <p className="muted small">Only the shop's books change. No money moves and nobody is contacted.</p>
      )}

      <p className="card-reason">
        <span className="muted">{fromCustomer ? "Customer's request:" : "AI's reason:"}</span> {action.reason}
      </p>

      {open ? (
        <div className="card-actions">
          {action.status === 'AwaitingReturn' ? (
            <button className="button primary" disabled={busy} onClick={() => onReturnReceived(action.id)}>
              {busy ? 'Working…' : 'Return received'}
            </button>
          ) : (
            <button className="button primary" disabled={busy} onClick={() => onApprove(action.id)}>
              {busy ? 'Working…' : approveLabel[action.type]}
            </button>
          )}
          <button className="button" disabled={busy} onClick={() => onReject(action.id)}>
            Reject
          </button>
        </div>
      ) : (
        <div className={`card-result result-${action.status.toLowerCase()}`}>
          <strong>{statusLabel[action.status]}</strong>
          {action.status === 'Executed' && action.type === 'Refund' && action.payPalResultId && (
            <> · PayPal refund ID <code>{action.payPalResultId}</code> · posted to the books</>
          )}
          {action.status === 'Executed' && action.type === 'InvoiceReminder' && <> · PayPal emailed the reminder to the customer</>}
          {action.status === 'Executed' && action.type === 'BooksCorrection' && <> · recorded in the books</>}
          {action.error && <div className="error-text">{action.error}</div>}
          {action.decidedAt && <div className="muted small">{formatTime(action.decidedAt)}</div>}
        </div>
      )}
    </article>
  )
}
