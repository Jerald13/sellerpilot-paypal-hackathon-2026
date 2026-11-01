import { useCallback, useEffect, useState } from 'react'
import { api, formatTime, type AuditEntry } from '../api'

const filters: Record<string, (e: AuditEntry) => boolean> = {
  All: () => true,
  'Tool calls': (e) => e.event.startsWith('Tool'),
  Proposals: (e) => e.event === 'ActionProposed',
  Decisions: (e) => ['ActionApproved', 'ActionRejected', 'ActionExecuted', 'ActionFailed'].includes(e.event),
  Guardrails: (e) => ['GuardrailBlocked', 'InjectionSuspected', 'LlmRefusal', 'AgentStepLimitReached'].includes(e.event),
}

export function AuditLog() {
  const [entries, setEntries] = useState<AuditEntry[]>([])
  const [filter, setFilter] = useState('All')
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      api
        .audit()
        .then((list) => {
          setEntries(list)
          setError(null)
        })
        .catch((e: Error) => setError(e.message)),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  const visible = entries.filter(filters[filter])

  return (
    <section className="panel audit">
      <div className="panel-title">
        <h2>Audit log</h2>
        <div className="row">
          <select value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter events">
            {Object.keys(filters).map((f) => (
              <option key={f}>{f}</option>
            ))}
          </select>
          <button className="button" onClick={() => void load()}>
            Refresh
          </button>
        </div>
      </div>
      <p className="muted small">Every tool call, proposal, guardrail block, approval and PayPal execution is recorded here.</p>
      {error && <div className="error-text">{error}</div>}
      <table>
        <thead>
          <tr>
            <th>Time</th>
            <th>Actor</th>
            <th>Event</th>
            <th>Details</th>
          </tr>
        </thead>
        <tbody>
          {visible.map((e) => (
            <tr key={e.id}>
              <td className="nowrap">{formatTime(e.timestamp)}</td>
              <td>
                <span className={`badge actor-${e.actor}`}>{e.actor}</span>
              </td>
              <td className={`event event-${e.event}`}>{e.event}</td>
              <td>
                <details>
                  <summary>{summarize(e)}</summary>
                  <pre>{JSON.stringify(e.details, null, 2)}</pre>
                </details>
              </td>
            </tr>
          ))}
          {visible.length === 0 && (
            <tr>
              <td colSpan={4} className="empty">
                No entries yet.
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </section>
  )
}

function summarize(entry: AuditEntry): string {
  const d = (entry.details ?? {}) as Record<string, unknown>
  const parts = [d.tool, d.type, d.targetId ?? d.captureId ?? d.invoiceId, d.amount !== undefined ? `${d.amount} ${d.currency ?? ''}` : null, d.paypalResultId, d.error]
    .filter((p) => p !== undefined && p !== null && p !== '')
    .map(String)
  return parts.length > 0 ? parts.join(' · ') : 'details'
}
