import { useCallback, useEffect, useState } from 'react'
import { api, formatTime, type EvalCaseResult, type EvalEstimate, type EvalLatest } from '../api'
import { Markdown } from './Markdown'

const usd = (value: number | null | undefined, digits = 4) => (value === null || value === undefined ? 'n/a' : `$${value.toFixed(digits)}`)

/** Runs eval/cases.json against the real model and sandbox (same tool as the terminal) and shows why each case passed or failed. */
export function EvalView() {
  const [latest, setLatest] = useState<EvalLatest | null>(null)
  const [estimate, setEstimate] = useState<EvalEstimate | null>(null)
  const [category, setCategory] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [open, setOpen] = useState<string | null>(null)
  const [onlyFailures, setOnlyFailures] = useState(false)

  const load = useCallback(async () => {
    try {
      setLatest(await api.evalLatest())
    } catch (e) {
      setError((e as Error).message)
    }
  }, [])

  useEffect(() => {
    api.evalLatest().then(setLatest).catch((e: Error) => setError(e.message))
  }, [])

  useEffect(() => {
    api.evalEstimate(category || undefined).then(setEstimate).catch(() => setEstimate(null))
  }, [category, latest?.running])

  // Poll while a run is going; every finished case shows up live.
  useEffect(() => {
    if (!latest?.running) return
    const timer = setInterval(() => void load(), 2000)
    return () => clearInterval(timer)
  }, [latest?.running, load])

  const start = async () => {
    const cases = estimate?.cases ?? 0
    const cost = estimate?.estimatedUsd !== null && estimate?.estimatedUsd !== undefined ? ` Estimated cost: about ${usd(estimate.estimatedUsd, 2)} (${estimate.basis}).` : ''
    if (!window.confirm(`Run ${cases} eval case${cases === 1 ? '' : 's'} against ${estimate?.model ?? 'the model'}? This calls the real LLM.${cost}`)) return
    setError(null)
    try {
      await api.evalStart(category || undefined)
      await load()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  const cancel = async () => {
    await api.evalCancel().catch((e: Error) => setError(e.message))
    await load()
  }

  const report = latest?.report
  const running = latest?.running ?? false
  const results = report?.results ?? []
  // Reports written before cost tracking have no totals or progress fields.
  const totals = report?.totals ?? { costUsd: null, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0 }
  const completed = report?.completed ?? results.length
  const shown = onlyFailures ? results.filter((r) => r.status === 'FAIL' || r.status === 'ERROR') : results
  const total = report?.info.totalCases ?? 0

  return (
    <section className="panel data-view">
      <div className="panel-title">
        <h2>Evaluation: is the AI accurate?</h2>
        <div className="row">
          <select value={category} onChange={(e) => setCategory(e.target.value)} disabled={running} aria-label="Category">
            <option value="">All categories</option>
            {Object.entries(estimate?.byCategory ?? {}).map(([c, n]) => (
              <option key={c} value={c}>
                {c} ({n})
              </option>
            ))}
          </select>
          {running ? (
            <button className="button danger" onClick={() => void cancel()}>
              Stop run
            </button>
          ) : (
            <button className="button primary" onClick={() => void start()}>
              Run {estimate ? `${estimate.cases} cases` : 'eval'}
              {estimate?.estimatedUsd !== null && estimate?.estimatedUsd !== undefined && ` · ~${usd(estimate.estimatedUsd, 2)}`}
            </button>
          )}
        </div>
      </div>
      <p className="muted small">
        Each case sends a prompt to the real assistant with real sandbox data and checks the outcome with fixed rules (tools called, proposals, amounts,
        reply text). PayPal writes are refused during the eval, so nothing is ever refunded. Same as <code>dotnet run --project tools/SellerPilot.Eval</code>.
      </p>
      {error && <div className="error-text">{error}</div>}
      {latest && latest.output.length > 0 && !running && latest.lastExitCode !== 0 && (
        <pre className="eval-output">{latest.output.join('\n')}</pre>
      )}

      {!report && !running && <div className="empty">No eval run yet. Pick a category (or all) and click Run.</div>}

      {report && (
        <>
          {running && (
            <div className="progress" aria-label="progress">
              <div className="progress-bar" style={{ width: `${total ? (completed / total) * 100 : 0}%` }} />
              <span className="small">
                Running… {completed}/{total} cases
              </span>
            </div>
          )}
          <div className="summary-tiles">
            <div className="tile">
              <div className={`tile-value ${report.score.passed === report.score.scored ? 'ok' : 'bad'}`}>
                {report.score.passed}/{report.score.scored}
              </div>
              <div className="muted small">passed{report.score.skipped > 0 && ` · ${report.score.skipped} skipped`}</div>
            </div>
            <div className="tile">
              <div className="tile-value">{usd(totals.costUsd)}</div>
              <div className="muted small">estimated cost</div>
            </div>
            <div className="tile">
              <div className="tile-value">{(totals.inputTokens + totals.cacheReadTokens + totals.cacheWriteTokens).toLocaleString()}</div>
              <div className="muted small">tokens in ({totals.cacheReadTokens.toLocaleString()} cached)</div>
            </div>
            <div className="tile">
              <div className="tile-value">{totals.outputTokens.toLocaleString()}</div>
              <div className="muted small">tokens out</div>
            </div>
          </div>
          <div className="row muted small">
            <span>
              {report.info.provider} <code>{report.info.model}</code> · started {formatTime(report.info.startedAt)} · seed run {report.info.seedRunTag ?? 'none'}
            </span>
            <label className="row">
              <input type="checkbox" checked={onlyFailures} onChange={(e) => setOnlyFailures(e.target.checked)} /> only failures
            </label>
          </div>

          <table className="table">
            <thead>
              <tr>
                <th>Case</th>
                <th>Category</th>
                <th>Result</th>
                <th>Steps</th>
                <th className="num">Time</th>
                <th className="num">Cost</th>
              </tr>
            </thead>
            <tbody>
              {shown.map((r) => (
                <CaseRow key={r.id} result={r} open={open === r.id} onToggle={() => setOpen(open === r.id ? null : r.id)} />
              ))}
            </tbody>
          </table>
        </>
      )}
    </section>
  )
}

function CaseRow({ result, open, onToggle }: { result: EvalCaseResult; open: boolean; onToggle: () => void }) {
  const o = result.observation
  return (
    <>
      <tr className="clickable" onClick={onToggle}>
        <td className="nowrap">
          {open ? '▾' : '▸'} <strong>{result.id}</strong>
        </td>
        <td>{result.category}</td>
        <td>
          <span className={`badge eval-${result.status.toLowerCase()}`}>{result.status === 'PASS' ? '✓ pass' : result.status === 'FAIL' ? '✕ fail' : result.status.toLowerCase()}</span>
        </td>
        <td className="small">{o?.toolCalls.map((t) => t.name).join(' → ') || '-'}</td>
        <td className="num">{(result.durationMs / 1000).toFixed(1)}s</td>
        <td className="num">{usd(result.costUsd)}</td>
      </tr>
      {open && (
        <tr className="detail-row">
          <td colSpan={6}>
            <div className="eval-detail">
              <div>
                <h4>Prompt{result.prompts.length > 1 ? 's' : ''}</h4>
                {result.prompts.map((p, i) => (
                  <blockquote key={i}>{p}</blockquote>
                ))}
              </div>
              {result.failures.length > 0 && (
                <div>
                  <h4>Why it {result.status === 'SKIP' ? 'was skipped' : 'failed'}</h4>
                  <ul className="error-text">
                    {result.failures.map((f, i) => (
                      <li key={i}>{f}</li>
                    ))}
                  </ul>
                </div>
              )}
              {o && o.toolCalls.length > 0 && (
                <div>
                  <h4>What the AI did (and what it saw)</h4>
                  <ol className="steps">
                    {o.toolCalls.map((t, i) => (
                      <li key={i}>
                        <strong>{t.name}</strong> <code>{JSON.stringify(t.arguments)}</code> {t.succeeded ? '' : <span className="error-text">✕ {t.error}</span>}
                        {t.result && <pre>{pretty(t.result)}</pre>}
                      </li>
                    ))}
                  </ol>
                </div>
              )}
              {o && o.proposals.length > 0 && (
                <div>
                  <h4>Proposals created (never executed in the eval)</h4>
                  {o.proposals.map((p, i) => (
                    <div key={i}>
                      {p.type} {p.amount !== null && `${p.currency} ${p.amount.toFixed(2)}`} on <code>{p.targetId}</code>
                      {p.reason && <div className="muted small">Reason: {p.reason}</div>}
                    </div>
                  ))}
                </div>
              )}
              {o && (o.offers ?? []).length > 0 && (
                <div>
                  <h4>Refund offers shown to the customer</h4>
                  {(o.offers ?? []).map((f, i) => (
                    <div key={i}>
                      {f.orderNumber}: {f.currency} {f.amount.toFixed(2)}
                      {f.returnRequired && ' (item must be returned first)'}
                    </div>
                  ))}
                </div>
              )}
              {o?.finalReply && (
                <div>
                  <h4>Final reply</h4>
                  <div className="message message-assistant">
                    <Markdown text={o.finalReply} />
                  </div>
                </div>
              )}
              {result.usage && (
                <div className="muted small">
                  {result.model} · {result.usage.inputTokens.toLocaleString()} in · {result.usage.cacheReadInputTokens.toLocaleString()} cached ·{' '}
                  {result.usage.outputTokens.toLocaleString()} out · {usd(result.costUsd)}
                </div>
              )}
            </div>
          </td>
        </tr>
      )}
    </>
  )
}

function pretty(json: string) {
  try {
    const text = JSON.stringify(JSON.parse(json), null, 2)
    return text.length > 1500 ? `${text.slice(0, 1500)}\n…` : text
  } catch {
    return json
  }
}
