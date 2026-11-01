import { useCallback, useEffect, useRef, useState } from 'react'
import { api, type Health, type PendingAction } from './api'
import { ApprovalsPanel } from './components/ApprovalsPanel'
import { AuditLog } from './components/AuditLog'
import { ChatPanel, type ChatMessage } from './components/ChatPanel'

type Tab = 'assistant' | 'audit'

const conversationKey = 'sellerpilot.conversationId'

export default function App() {
  const [tab, setTab] = useState<Tab>('assistant')
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [conversationId, setConversationId] = useState<string | null>(() => sessionStorage.getItem(conversationKey))
  const [chatBusy, setChatBusy] = useState(false)
  const [actions, setActions] = useState<PendingAction[]>([])
  const [busyIds, setBusyIds] = useState<Set<string>>(new Set())
  const [health, setHealth] = useState<Health | null>(null)
  const nextId = useRef(1)

  const addMessage = (message: Omit<ChatMessage, 'id'>) =>
    setMessages((current) => [...current, { ...message, id: nextId.current++ }])

  const refreshActions = useCallback(async () => {
    try {
      setActions(await api.actions())
    } catch {
      // The API may still be starting; the next refresh will catch up.
    }
  }, [])

  useEffect(() => {
    api.actions().then(setActions).catch(() => setActions([]))
    api.health().then(setHealth).catch(() => setHealth(null))
  }, [])

  const send = async (text: string) => {
    addMessage({ role: 'user', text })
    setChatBusy(true)
    try {
      const response = await api.chat(text, conversationId)
      setConversationId(response.conversationId)
      sessionStorage.setItem(conversationKey, response.conversationId)
      addMessage({
        role: 'assistant',
        text: response.reply,
        toolCalls: response.toolCalls,
        flags: response.securityFlags,
        proposals: response.proposedActions.length,
      })
      if (response.proposedActions.length > 0) await refreshActions()
    } catch (e) {
      addMessage({ role: 'error', text: `Something went wrong: ${(e as Error).message}` })
    } finally {
      setChatBusy(false)
    }
  }

  const newChat = () => {
    setMessages([])
    setConversationId(null)
    sessionStorage.removeItem(conversationKey)
  }

  const setBusy = (id: string, busy: boolean) =>
    setBusyIds((current) => {
      const next = new Set(current)
      if (busy) next.add(id)
      else next.delete(id)
      return next
    })

  const decide = async (id: string, decision: 'approve' | 'reject') => {
    setBusy(id, true)
    try {
      const updated = decision === 'approve' ? await api.approve(id) : await api.reject(id)
      setActions((current) => current.map((a) => (a.id === id ? updated : a)))
    } catch (e) {
      addMessage({ role: 'error', text: `Could not ${decision} the action: ${(e as Error).message}` })
      await refreshActions()
    } finally {
      setBusy(id, false)
    }
  }

  const approveAll = async (ids: string[]) => {
    // One at a time: each approval re-validates against PayPal before executing.
    for (const id of ids) {
      await decide(id, 'approve')
    }
  }

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <span className="logo" aria-hidden>
            ✈
          </span>
          <div>
            <h1>SellerPilot</h1>
            <div className="muted small">AI payment ops for small sellers · PayPal sandbox</div>
          </div>
        </div>
        <nav className="tabs">
          <button className={tab === 'assistant' ? 'active' : ''} onClick={() => setTab('assistant')}>
            Assistant
          </button>
          <button className={tab === 'audit' ? 'active' : ''} onClick={() => setTab('audit')}>
            Audit log
          </button>
        </nav>
        <HealthBadges health={health} />
        <ThemeToggle />
      </header>

      {tab === 'assistant' ? (
        <main className="layout">
          <ChatPanel messages={messages} busy={chatBusy} onSend={(t) => void send(t)} onNewChat={newChat} />
          <ApprovalsPanel
            actions={actions}
            busyIds={busyIds}
            onApprove={(id) => void decide(id, 'approve')}
            onReject={(id) => void decide(id, 'reject')}
            onApproveAll={(ids) => void approveAll(ids)}
          />
        </main>
      ) : (
        <main className="layout single">
          <AuditLog />
        </main>
      )}
    </div>
  )
}

const themeKey = 'sellerpilot.theme'

function ThemeToggle() {
  // index.html already applied the saved theme (dark by default) before React started.
  const [theme, setTheme] = useState(() => document.documentElement.dataset.theme ?? 'dark')
  const toggle = () => {
    const next = theme === 'dark' ? 'light' : 'dark'
    document.documentElement.dataset.theme = next
    localStorage.setItem(themeKey, next)
    setTheme(next)
  }
  return (
    <button className="button ghost theme-toggle" onClick={toggle} title={`Switch to ${theme === 'dark' ? 'light' : 'dark'} mode`}>
      {theme === 'dark' ? '☀ Light' : '☾ Dark'}
    </button>
  )
}

function HealthBadges({ health }: { health: Health | null }) {
  if (!health) return <div className="health"><span className="dot bad" /> API offline</div>
  const paypalTitle = health.payPal.error ?? (health.payPal.features?.transactionSearch === false ? 'Transaction Search is not enabled for this sandbox app' : 'Sandbox credentials OK')
  return (
    <div className="health">
      <span title={paypalTitle}>
        <span className={`dot ${health.payPal.ok ? 'ok' : 'bad'}`} /> PayPal {health.payPal.environment}
      </span>
      <span title={health.llm.configured ? health.llm.model : 'No API key configured'}>
        <span className={`dot ${health.llm.configured ? 'ok' : 'bad'}`} /> {health.llm.provider}
      </span>
    </div>
  )
}
