import { useEffect, useRef, useState, type FormEvent } from 'react'
import type { SecurityFlag, ToolCall } from '../api'
import { Markdown } from './Markdown'

export interface ChatMessage {
  id: number
  role: 'user' | 'assistant' | 'error'
  text: string
  toolCalls?: ToolCall[]
  flags?: SecurityFlag[]
  proposals?: number
}

interface Props {
  messages: ChatMessage[]
  busy: boolean
  onSend: (text: string) => void
  onNewChat: () => void
}

const suggestions = [
  'Do my books match PayPal?',
  'John Doe says his wall hanging arrived damaged. Can you sort out a refund?',
  'Which invoices are still unpaid?',
  'Send a polite reminder to all of them.',
]

export function ChatPanel({ messages, busy, onSend, onNewChat }: Props) {
  const [draft, setDraft] = useState('')
  const endRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages, busy])

  const submit = (event: FormEvent) => {
    event.preventDefault()
    const text = draft.trim()
    if (!text || busy) return
    onSend(text)
    setDraft('')
  }

  return (
    <section className="panel chat">
      <div className="panel-title">
        <h2>
          AI assistant <span className="badge badge-ai">AI</span>
        </h2>
        <button className="button ghost" onClick={onNewChat} disabled={busy}>
          New chat
        </button>
      </div>
      <p className="chat-note">SellerPilot is an AI. It reads your PayPal data and proposes actions, but it never moves money: you approve every refund or reminder in Approvals.</p>

      <div className="messages">
        {messages.length === 0 && (
          <div className="empty">
            Ask about your PayPal orders, payments, unpaid invoices or refunds.
            <div className="suggestions">
              {suggestions.map((s) => (
                <button key={s} className="chip" onClick={() => onSend(s)} disabled={busy}>
                  {s}
                </button>
              ))}
            </div>
          </div>
        )}

        {messages.map((m) => (
          <div key={m.id} className={`message message-${m.role}`}>
            {m.role === 'assistant' && <div className="message-label">SellerPilot AI</div>}
            {m.role === 'assistant' ? <Markdown text={m.text} /> : <p>{m.text}</p>}

            {m.flags && m.flags.length > 0 && (
              <div className="warning">
                <strong>Possible prompt injection blocked.</strong> This text in PayPal data was withheld from the AI:
                {m.flags.map((f, i) => (
                  <blockquote key={i}>
                    “{f.excerpt}” <span className="muted">— {f.source}</span>
                  </blockquote>
                ))}
              </div>
            )}

            {m.toolCalls && m.toolCalls.length > 0 && (
              <div className="tool-calls">
                {m.toolCalls.map((t, i) => (
                  <span key={i} className={`tool ${t.succeeded ? 'ok' : 'failed'}`} title={t.error ?? JSON.stringify(t.arguments)}>
                    {t.succeeded ? '✓' : '✕'} {t.name}
                  </span>
                ))}
              </div>
            )}

            {m.proposals !== undefined && m.proposals > 0 && (
              <div className="muted small">
                {m.proposals} proposal{m.proposals > 1 ? 's' : ''} added to Approvals →
              </div>
            )}
          </div>
        ))}

        {busy && <div className="message message-assistant typing">SellerPilot AI is checking PayPal…</div>}
        <div ref={endRef} />
      </div>

      <form className="composer" onSubmit={submit}>
        <input
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          placeholder="e.g. Customer for order 5O190127TN364715T wants a refund. What should I do?"
          maxLength={2000}
          disabled={busy}
          aria-label="Message"
        />
        <button className="button primary" type="submit" disabled={busy || !draft.trim()}>
          Send
        </button>
      </form>
    </section>
  )
}
