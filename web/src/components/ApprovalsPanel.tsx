import { useEffect, useState } from 'react'
import type { PendingAction } from '../api'
import { ActionCard } from './ActionCard'

interface Props {
  actions: PendingAction[]
  busyIds: Set<string>
  onApprove: (id: string) => void
  onReject: (id: string) => void
  onReturnReceived: (id: string) => void
  onApproveAll: (ids: string[]) => void
}

export function ApprovalsPanel({ actions, busyIds, onApprove, onReject, onReturnReceived, onApproveAll }: Props) {
  const pending = actions.filter((a) => a.status === 'Pending')
  const awaitingReturn = actions.filter((a) => a.status === 'AwaitingReturn')
  const decided = actions.filter((a) => a.status !== 'Pending' && a.status !== 'AwaitingReturn').slice(0, 8)
  const [confirmAll, setConfirmAll] = useState(false)

  // The "approve all" confirmation expires so a stray second click later can't approve by accident.
  useEffect(() => {
    if (!confirmAll) return
    const timer = setTimeout(() => setConfirmAll(false), 5000)
    return () => clearTimeout(timer)
  }, [confirmAll])

  const card = (action: PendingAction, busy: boolean) => (
    <ActionCard key={action.id} action={action} busy={busy} onApprove={onApprove} onReject={onReject} onReturnReceived={onReturnReceived} />
  )

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
        The AI and your customers can only propose. Nothing is refunded, sent or booked until you approve it here.
      </p>

      {pending.length === 0 && awaitingReturn.length === 0 && (
        <div className="empty">No proposals waiting. Ask the assistant to refund an order or remind a customer.</div>
      )}
      {pending.map((action) => card(action, busyIds.has(action.id)))}

      {awaitingReturn.length > 0 && (
        <>
          <h3 className="subtle-heading">Waiting for a return</h3>
          {awaitingReturn.map((action) => card(action, busyIds.has(action.id)))}
        </>
      )}

      {decided.length > 0 && (
        <>
          <h3 className="subtle-heading">Recently decided</h3>
          {decided.map((action) => card(action, false))}
        </>
      )}
    </section>
  )
}
