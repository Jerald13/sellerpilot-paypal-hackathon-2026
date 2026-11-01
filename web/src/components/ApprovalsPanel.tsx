import { useEffect, useState } from 'react'
import type { PendingAction } from '../api'
import { ActionCard } from './ActionCard'

interface Props {
  actions: PendingAction[]
  busyIds: Set<string>
  onApprove: (id: string) => void
  onReject: (id: string) => void
  onApproveAll: (ids: string[]) => void
}

export function ApprovalsPanel({ actions, busyIds, onApprove, onReject, onApproveAll }: Props) {
  const pending = actions.filter((a) => a.status === 'Pending')
  const decided = actions.filter((a) => a.status !== 'Pending').slice(0, 8)
  const [confirmAll, setConfirmAll] = useState(false)

  // The "approve all" confirmation expires so a stray second click later can't approve by accident.
  useEffect(() => {
    if (!confirmAll) return
    const timer = setTimeout(() => setConfirmAll(false), 5000)
    return () => clearTimeout(timer)
  }, [confirmAll])

  return (
    <section className="panel approvals">
      <div className="panel-title">
        <h2>Approvals {pending.length > 0 && <span className="count">{pending.length}</span>}</h2>
        {pending.length > 1 && (
          <button
            className={`button ${confirmAll ? 'danger' : 'primary'}`}
            disabled={pending.some((a) => busyIds.has(a.id))}
            onClick={() => {
              if (confirmAll) {
                setConfirmAll(false)
                onApproveAll(pending.map((a) => a.id))
              } else {
                setConfirmAll(true)
              }
            }}
          >
            {confirmAll ? `Click again to approve ${pending.length}` : `Approve all (${pending.length})`}
          </button>
        )}
      </div>
      <p className="muted small">
        The AI can only propose. Nothing is refunded or sent until you approve it here.
      </p>

      {pending.length === 0 && <div className="empty">No proposals waiting. Ask the assistant to refund an order or remind a customer.</div>}
      {pending.map((action) => (
        <ActionCard key={action.id} action={action} busy={busyIds.has(action.id)} onApprove={onApprove} onReject={onReject} />
      ))}

      {decided.length > 0 && (
        <>
          <h3 className="subtle-heading">Recently decided</h3>
          {decided.map((action) => (
            <ActionCard key={action.id} action={action} busy={false} onApprove={onApprove} onReject={onReject} />
          ))}
        </>
      )}
    </section>
  )
}
