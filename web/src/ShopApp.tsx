import { useEffect, useRef, useState, type FormEvent } from 'react'
import { ThemeToggle } from './App'
import { AppLoader } from './components/AppLoader'
import {
  api,
  ApiError,
  formatDate,
  formatMoney,
  formatTime,
  type CurrentUser,
  type MyOrder,
  type MyRefundRequest,
  type Product,
  type RefundOffer,
  type ShopInfo,
} from './api'
import { LoginForm } from './components/LoginForm'
import { ProductArt } from './components/ProductArt'
import { Markdown } from './components/Markdown'

const demoCustomers = ['john.doe@example.com', 'alice.carter@example.com', 'ben.miller@example.com', 'chloe.bennett@example.com']

/** The customer-facing shop: products, a customer's own orders, and a support chat that can offer refunds. */
export default function ShopApp() {
  const [info, setInfo] = useState<ShopInfo | null>(null)
  const [products, setProducts] = useState<Product[]>([])
  const [user, setUser] = useState<CurrentUser | null | undefined>(undefined)

  useEffect(() => {
    api.shopInfo().then(setInfo).catch(() => setInfo(null))
    api.products().then(setProducts).catch(() => setProducts([]))
    api.me().then(setUser).catch(() => setUser(null))
  }, [])

  const signOut = async () => {
    await api.logout().catch(() => undefined)
    setUser(null)
  }

  return (
    <div className="app shop">
      <header className="topbar">
        <div className="brand">
          <span className="logo shop-logo" aria-hidden>
            🧵
          </span>
          <div>
            <h1>{info?.shopName ?? 'Shop'}</h1>
            <div className="muted small">Handmade textiles · shipped to the US, Singapore and Malaysia · demo shop on the PayPal sandbox</div>
          </div>
        </div>
        <div className="topbar-right">
          {user && (
            <span className="user">
              <span className="user-chip" title={user.email}>
                <span className="avatar" aria-hidden>
                  {user.name.charAt(0).toUpperCase()}
                </span>
                {user.name}
              </span>
              <button className="button outline" onClick={() => void signOut()}>
                Sign out
              </button>
            </span>
          )}
          <a className="button outline" href="/" title="SellerPilot back office (for the seller)">
            Seller back office ↗
          </a>
          <ThemeToggle />
        </div>
      </header>

      {user === undefined && <AppLoader label="Opening the shop…" />}
      {user === null && (
        <main className="layout shop-home">
          <section className="panel">
            <h2>Our products</h2>
            <div className="products">
              {products.map((p) => (
                <div key={p.sku} className="product">
                  <ProductArt sku={p.sku} />
                  <div className="product-name">{p.name}</div>
                  <div className="product-price">{formatMoney(p.price, p.currency)}</div>
                  <div className="muted small">{p.returnPolicy}</div>
                </div>
              ))}
              {products.length === 0 && <div className="muted">No products yet. Run the seed tool, then restart the API.</div>}
            </div>
          </section>
          <LoginForm
            title="Customer sign-in"
            subtitle="See your orders and get help with a refund."
            demoEmails={demoCustomers}
            onSignedIn={setUser}
          />
        </main>
      )}
      {user && user.role === 'seller' && (
        <main className="layout single centered">
          <div className="panel">
            You're signed in as the seller. <a href="/">Open the back office</a>, or sign out to try the shop as a customer.
          </div>
        </main>
      )}
      {user && user.role === 'customer' && <CustomerArea />}
    </div>
  )
}

interface ShopMessage {
  id: number
  role: 'customer' | 'assistant' | 'system' | 'error'
  text: string
  offers?: RefundOffer[]
}

const suggestions = ['What did I order?', 'One of my items arrived damaged. Can I get a refund?', 'Can I still return the scarf I bought in August?']

function CustomerArea() {
  const [orders, setOrders] = useState<MyOrder[]>([])
  const [requests, setRequests] = useState<MyRefundRequest[]>([])
  const [messages, setMessages] = useState<ShopMessage[]>([])
  const [conversationId, setConversationId] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [usedOffers, setUsedOffers] = useState<Set<string>>(new Set())
  const [draft, setDraft] = useState('')
  const nextId = useRef(1)
  const listRef = useRef<HTMLDivElement>(null)

  const add = (m: Omit<ShopMessage, 'id'>) => setMessages((current) => [...current, { ...m, id: nextId.current++ }])
  const refresh = () => {
    api.myOrders().then(setOrders).catch(() => undefined)
    api.myRefundRequests().then(setRequests).catch(() => undefined)
  }

  useEffect(() => {
    api.myOrders().then(setOrders).catch(() => undefined)
    api.myRefundRequests().then(setRequests).catch(() => undefined)
  }, [])

  useEffect(() => {
    // Scroll only the message list; scrollIntoView would also scroll the whole page down.
    const list = listRef.current
    list?.scrollTo({ top: list.scrollHeight, behavior: 'smooth' })
  }, [messages, busy])

  const send = async (text: string) => {
    add({ role: 'customer', text })
    setBusy(true)
    try {
      const response = await api.shopChat(text, conversationId)
      setConversationId(response.conversationId)
      add({ role: 'assistant', text: response.reply, offers: response.refundOffers })
    } catch (e) {
      add({ role: 'error', text: `Something went wrong: ${(e as Error).message}` })
    } finally {
      setBusy(false)
    }
  }

  const offerKey = (o: RefundOffer, messageId: number) => `${messageId}:${o.orderNumber}:${o.amount}`

  const request = async (offer: RefundOffer, messageId: number) => {
    setUsedOffers((current) => new Set(current).add(offerKey(offer, messageId)))
    try {
      const created = await api.requestRefund(offer)
      add({
        role: 'system',
        text:
          created.status === 'AwaitingReturn'
            ? `Request sent for ${formatMoney(created.amount, created.currency)} on ${created.orderNumber}. Please send the item back; the refund is approved when it arrives.`
            : `Request sent for ${formatMoney(created.amount, created.currency)} on ${created.orderNumber}. The shop owner will review it.`,
      })
      refresh()
    } catch (e) {
      // Let the customer try again once the problem is fixed.
      setUsedOffers((current) => {
        const next = new Set(current)
        next.delete(offerKey(offer, messageId))
        return next
      })
      const signedOut = e instanceof ApiError && (e.status === 401 || e.status === 403)
      add({
        role: 'error',
        text: signedOut
          ? 'The request could not be sent: you are no longer signed in as this customer (perhaps you signed in as someone else in another tab). Reload the page and sign in again.'
          : `The request could not be sent: ${(e as Error).message}`,
      })
    }
  }

  const submit = (event: FormEvent) => {
    event.preventDefault()
    const text = draft.trim()
    if (!text || busy) return
    void send(text)
    setDraft('')
  }

  return (
    <main className="layout">
      <section className="panel shop-orders">
        <h2>My orders</h2>
        {orders.length === 0 && <div className="muted">No orders on this account.</div>}
        {orders.map((o) => (
          <article key={o.orderNumber} className="card">
            <header className="card-header">
              <strong>{o.orderNumber}</strong>
              <span className="card-amount">{formatMoney(o.total, o.currency)}</span>
            </header>
            <div className="order-items">
              {o.items.map((i) => (
                <div key={i.sku} className="order-item">
                  <ProductArt sku={i.sku} className="thumb" />
                  <span>
                    {i.quantity} × {i.name}
                  </span>
                </div>
              ))}
            </div>
            <div className="muted small">
              Ordered {formatDate(o.placedAt)} · {o.deliveredAt ? `delivered ${formatDate(o.deliveredAt)}` : orderStatus[o.status] ?? o.status} · paid with{' '}
              {o.paidWith} · {o.returnPolicy}
              {o.discount > 0 && (
                <>
                  {' '}
                  · discount {o.discountCode} −{formatMoney(o.discount, o.currency)}
                </>
              )}
              {o.refundedSoFar > 0 && <> · refunded {formatMoney(o.refundedSoFar, o.currency)}</>}
            </div>
          </article>
        ))}

        {requests.length > 0 && (
          <>
            <h3 className="subtle-heading">My refund requests</h3>
            {requests.map((r) => (
              <div key={r.id} className="request-row">
                <span>
                  {r.orderNumber} · {formatMoney(r.amount, r.currency)}
                </span>
                <span className={`badge req-${r.status.toLowerCase()}`}>{requestStatus[r.status] ?? r.status}</span>
                <span className="muted small">{formatTime(r.createdAt)}</span>
              </div>
            ))}
          </>
        )}
      </section>

      <section className="panel chat">
        <div className="panel-title">
          <h2>
            Chat with our AI assistant <span className="badge badge-ai">AI</span>
          </h2>
        </div>
        <p className="chat-note">You are chatting with an AI, not the shop owner. It can check your orders and suggest a refund; the shop owner reviews every refund before any money moves.</p>
        <div className="messages" ref={listRef}>
          {messages.length === 0 && (
            <div className="empty">
              Ask about your orders or a refund.
              <div className="suggestions">
                {suggestions.map((s) => (
                  <button key={s} className="chip" onClick={() => void send(s)} disabled={busy}>
                    {s}
                  </button>
                ))}
              </div>
            </div>
          )}
          {messages.map((m) => (
            <div key={m.id} className={`message message-${m.role === 'customer' ? 'user' : m.role === 'error' ? 'error' : 'assistant'} ${m.role === 'system' ? 'message-system' : ''}`}>
              {m.role === 'assistant' && <div className="message-label">AI assistant</div>}
              {m.role === 'assistant' ? <Markdown text={m.text} /> : <p>{m.text}</p>}
              {m.offers?.map((o) => (
                <div key={offerKey(o, m.id)} className="offer">
                  <button className="button primary" disabled={usedOffers.has(offerKey(o, m.id))} onClick={() => void request(o, m.id)}>
                    {usedOffers.has(offerKey(o, m.id)) ? 'Requested' : `Request refund of ${formatMoney(o.amount, o.currency)}`}
                  </button>
                  <span className="muted small">
                    Order {o.orderNumber}
                    {o.returnRequired ? ' · the item must be sent back first' : ''}
                  </span>
                </div>
              ))}
            </div>
          ))}
          {busy && <div className="message message-assistant typing">The AI assistant is checking your order…</div>}
        </div>
        <form className="composer" onSubmit={submit}>
          <input value={draft} onChange={(e) => setDraft(e.target.value)} placeholder="e.g. My cushion covers arrived torn. Can I get USD 60 back?" maxLength={2000} disabled={busy} aria-label="Message" />
          <button className="button primary" type="submit" disabled={busy || !draft.trim()}>
            Send
          </button>
        </form>
      </section>
    </main>
  )
}

const orderStatus: Record<string, string> = {
  AwaitingPayment: 'awaiting payment',
  Paid: 'paid, not shipped yet',
  Shipped: 'shipped',
  Cancelled: 'cancelled',
}

const requestStatus: Partial<Record<MyRefundRequest['status'], string>> = {
  Pending: 'Waiting for the shop',
  AwaitingReturn: 'Waiting for your return',
  Approved: 'Approved',
  Executed: 'Refunded',
  Rejected: 'Declined',
  Failed: 'Could not be refunded',
  NotSent: 'Approved (public demo: not refunded)',
}
