import { Fragment, useEffect, useState } from 'react'
import { api, formatDate, formatMoney, formatTime, type ShopOrder } from '../api'
import { ProductArt } from './ProductArt'

/** The shop's own records: orders and their payment history (the books). */
export function OrdersView() {
  const [orders, setOrders] = useState<ShopOrder[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [open, setOpen] = useState<string | null>(null)

  useEffect(() => {
    api.orders().then(setOrders).catch((e: Error) => setError(e.message))
  }, [])

  if (error) return <section className="panel"><div className="error-text">{error}</div></section>
  if (!orders) return <section className="panel"><div className="muted">Loading orders…</div></section>

  return (
    <section className="panel data-view">
      <div className="panel-title">
        <h2>Orders &amp; payment history</h2>
        <span className="muted small">{orders.length} orders · the shop's books, compared with PayPal under Reconciliation</span>
      </div>
      {orders.length === 0 && <div className="empty">No shop orders yet. Run the seed tool, then restart the API.</div>}
      {orders.length > 0 && (
        <table className="table">
          <thead>
            <tr>
              <th>Order</th>
              <th>Customer</th>
              <th>Placed</th>
              <th>Status</th>
              <th className="num">Total</th>
              <th className="num">Paid (books)</th>
              <th className="num">Refunded (books)</th>
              <th>PayPal order</th>
            </tr>
          </thead>
          <tbody>
            {orders.map((o) => (
              <Fragment key={o.orderNumber}>
                <tr className="clickable" onClick={() => setOpen(open === o.orderNumber ? null : o.orderNumber)}>
                  <td className="nowrap">
                    {open === o.orderNumber ? '▾' : '▸'} <strong>{o.orderNumber}</strong>
                  </td>
                  <td>
                    {o.customerName} <span className="muted small">{o.customerCountry}</span>
                  </td>
                  <td className="nowrap">{formatDate(o.placedAt)}</td>
                  <td>
                    <span className={`badge status-${o.status.toLowerCase()}`}>{o.status}</span>
                  </td>
                  <td className="num">
                    {formatMoney(o.total, o.currency)}
                    {o.discount > 0 && <div className="muted small">−{formatMoney(o.discount, o.currency)} {o.discountCode}</div>}
                  </td>
                  <td className="num">{formatMoney(o.paymentsRecorded, o.currency)}</td>
                  <td className="num">{o.refundsRecorded > 0 ? formatMoney(o.refundsRecorded, o.currency) : '-'}</td>
                  <td>{o.payPalOrderId ? <code>{o.payPalOrderId}</code> : <span className="muted small">{o.paymentMethod}</span>}</td>
                </tr>
                {open === o.orderNumber && (
                  <tr className="detail-row">
                    <td colSpan={8}>
                      <div className="order-detail">
                        <div>
                          <h4>Items</h4>
                          {o.lines.map((l) => (
                            <div key={l.sku} className="order-item">
                              <ProductArt sku={l.sku} className="thumb" />
                              {l.quantity} × {l.name} · {formatMoney(l.unitPrice, o.currency)}{' '}
                              <span className="muted small">{l.returnWindowDays > 0 ? `${l.returnWindowDays}-day returns` : 'final sale'}</span>
                            </div>
                          ))}
                          <div className="muted small">
                            {o.customerEmail} · {o.deliveredAt ? `delivered ${formatDate(o.deliveredAt)}` : o.shippedAt ? `shipped ${formatDate(o.shippedAt)}` : 'not shipped'}
                          </div>
                        </div>
                        <div>
                          <h4>Payment history (books)</h4>
                          {o.ledger.length === 0 && <div className="muted small">No payments recorded.</div>}
                          {o.ledger.map((e) => (
                            <div key={e.id} className="ledger-entry">
                              <span className={`badge ${e.type === 'Refund' ? 'badge-refund' : 'badge-payment'}`}>{e.type}</span>{' '}
                              {e.type === 'Refund' ? '−' : '+'}
                              {formatMoney(e.amount, e.currency)} <span className="muted small">{formatTime(e.recordedAt)} · {e.source}</span>
                              {e.payPalTransactionId && (
                                <>
                                  {' '}
                                  <code>{e.payPalTransactionId}</code>
                                </>
                              )}
                            </div>
                          ))}
                        </div>
                      </div>
                    </td>
                  </tr>
                )}
              </Fragment>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}
