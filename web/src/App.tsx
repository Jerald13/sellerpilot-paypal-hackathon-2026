import { useCallback, useEffect, useRef, useState } from 'react'
import { api, ApiError, type CurrentUser, type Health, type PendingAction } from './api'
import { ApprovalsPanel } from './components/ApprovalsPanel'
import { AuditLog } from './components/AuditLog'
import { ChatPanel, type ChatMessage } from './components/ChatPanel'
import { EvalView } from './components/EvalView'
import { LoginForm } from './components/LoginForm'
import { OrdersView } from './components/OrdersView'
import { ReconciliationView } from './components/ReconciliationView'

type Tab = 'assistant' | 'orders' | 'reconciliation' | 'eval' | 'audit'

const conversationKey = 'sellerpilot.conversationId'

/** The seller's back office. Everything here needs the seller to be signed in. */
export default function App() {
  const [user, setUser] = useState<CurrentUser | null | undefined>(undefined)
  const [health, setHealth] = useState<Health | null>(null)

  useEffect(() => {
    api.me().then(setUser).catch(() => setUser(null))
    api.health().then(setHealth).catch(() => setHealth(null))
  }, [])

  const signOut = async () => {
    await api.logout().catch(() => undefined)
    sessionStorage.removeItem(conversationKey)
    setUser(null)
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
        <div className="topbar-right">
          <HealthBadges health={health} />
          <a className="button ghost" href="/shop" title="The customer-facing shop">
            Storefront ↗
          </a>
          {user && (
            <span className="user">
              {user.name}
              <button className="button ghost" onClick={() => void signOut()}>
                Sign out
              </button>
            </span>
          )}
          <ThemeToggle />
        </div>
      </header>

      {user === undefined && <main className="layout single"><div className="muted">Loading…</div></main>}
      {user === null && (
        <main className="layout single centered">
          {health?.auth?.configured === false && (
            <div className="warning">Sign-in is not configured: set <code>Shop__DemoPassword</code> in <code>.env</code> and restart the API.</div>
          )}
          <LoginForm
            title="Seller sign-in"
            subtitle="The back office: assistant, approvals, orders, reconciliation and audit log."
            demoEmails={['emma@sellerpilot.test']}
            onSignedIn={setUser}
          />
        </main>
      )}
      {user && user.role !== 'seller' && (
        <main className="layout single centered">
          <div className="panel">
            <p>
              You're signed in as a customer ({user.name}). The back office is for the seller. <a href="/shop">Go to the storefront</a> or{' '}
              <button className="button ghost" onClick={() => void signOut()}>
                sign out
              </button>
              .
            </p>
          </div>
        </main>
      )}
      {user && user.role === 'seller' && <BackOffice onSignedOut={() => setUser(null)} />}
    </div>
  )
}

function BackOffice({ onSignedOut }: { onSignedOut: () => void }) {
  const [tab, setTab] = useState<Tab>('assistant')
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [conversationId, setConversationId] = useState<string | null>(() => sessionStorage.getItem(conversationKey))
  const [chatBusy, setChatBusy] = useState(false)
  const [actions, setActions] = useState<PendingAction[]>([])
  const [busyIds, setBusyIds] = useState<Set<string>>(new Set())
  const nextId = useRef(1)

  const addMessage = (message: Omit<ChatMessage, 'id'>) =>
    setMessages((current) => [...current, { ...message, id: nextId.current++ }])

  const handleError = useCallback(
    (e: unknown) => {
      if (e instanceof ApiError && e.status === 401) onSignedOut()
    },
    [onSignedOut],
  )

  const refreshActions = useCallback(async () => {
    try {
      setActions(await api.actions())
    } catch (e) {
      handleError(e)
    }
  }, [handleError])

  useEffect(() => {
    api.actions().then(setActions).catch(handleError)
  }, [handleError])

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
      handleError(e)
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

  const decide = async (id: string, decision: 'approve' | 'reject' | 'return-received') => {
    setBusy(id, true)
    try {
      const updated =
        decision === 'approve' ? await api.approve(id) : decision === 'reject' ? await api.reject(id) : await api.returnReceived(id)
      setActions((current) => current.map((a) => (a.id === id ? updated : a)))
    } catch (e) {
      handleError(e)
      addMessage({ role: 'error', text: `Could not ${decision.replace('-', ' ')} the action: ${(e as Error).message}` })
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

  const askAssistant = (text: string) => {
    setTab('assistant')
    void send(text)
  }

  const tabs: { id: Tab; label: string }[] = [
    { id: 'assistant', label: 'Assistant' },
    { id: 'orders', label: 'Orders' },
    { id: 'reconciliation', label: 'Reconciliation' },
    { id: 'eval', label: 'Eval' },
    { id: 'audit', label: 'Audit log' },
  ]

  return (
    <>
      <nav className="tabs subnav">
        {tabs.map((t) => (
          <button
            key={t.id}
            className={tab === t.id ? 'active' : ''}
            onClick={() => {
              setTab(t.id)
              if (t.id === 'assistant') void refreshActions()
            }}
          >
            {t.label}
          </button>
        ))}
      </nav>

      {tab === 'assistant' && (
        <main className="layout">
          <ChatPanel messages={messages} busy={chatBusy} onSend={(t) => void send(t)} onNewChat={newChat} />
          <ApprovalsPanel
            actions={actions}
            busyIds={busyIds}
            onApprove={(id) => void decide(id, 'approve')}
            onReject={(id) => void decide(id, 'reject')}
            onReturnReceived={(id) => void decide(id, 'return-received')}
            onApproveAll={(ids) => void approveAll(ids)}
          />
        </main>
      )}
      {tab === 'orders' && (
        <main className="layout single">
          <OrdersView />
        </main>
      )}
      {tab === 'reconciliation' && (
        <main className="layout single">
          <ReconciliationView onAskAssistant={askAssistant} />
        </main>
      )}
      {tab === 'eval' && (
        <main className="layout single">
          <EvalView />
        </main>
      )}
      {tab === 'audit' && (
        <main className="layout single">
          <AuditLog />
        </main>
      )}
    </>
  )
}

const themeKey = 'sellerpilot.theme'

export function ThemeToggle() {
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
