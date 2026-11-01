// Typed client for the SellerPilot API. All money values are shown as given by the server.
// Sign-in uses an HttpOnly cookie; fetch sends it automatically because the UI and API share an origin (Vite proxy).

export type ActionType = 'Refund' | 'InvoiceReminder' | 'BooksCorrection'
export type ActionStatus = 'Pending' | 'Approved' | 'Rejected' | 'Executed' | 'Failed' | 'AwaitingReturn'

export interface SecurityFlag {
  source: string
  rules: string[]
  excerpt: string
}

export interface RefundDetails {
  orderId?: string | null
  customerName?: string | null
  capturedAmount?: number
  refundedBefore?: number
  remainingBefore?: number
  securityFlags?: SecurityFlag[]
  returnRequired?: boolean
  orderNumber?: string | null
}

export interface ReminderDetails {
  subject?: string | null
  note?: string
  invoiceNumber?: string
  customerName?: string | null
  dueDate?: string | null
  daysOverdue?: number
}

export interface BooksCorrectionDetails {
  orderNumber?: string
  entryType?: string
  payPalOrderId?: string | null
}

export interface PendingAction {
  id: string
  type: ActionType
  targetId: string
  amount: number | null
  currency: string | null
  reason: string
  summary: string
  details: (RefundDetails & ReminderDetails & BooksCorrectionDetails) | null
  status: ActionStatus
  payPalResultId: string | null
  error: string | null
  createdAt: string
  decidedAt: string | null
  requestedBy: 'agent' | 'customer'
  customerId: number | null
  shopOrderId: number | null
}

export interface ToolCall {
  name: string
  arguments: unknown
  succeeded: boolean
  error: string | null
  durationMs: number
}

export interface ChatResponse {
  conversationId: string
  reply: string
  proposedActions: PendingAction[]
  toolCalls: ToolCall[]
  securityFlags: SecurityFlag[]
  stepLimitReached: boolean
  refused: boolean
}

export interface AuditEntry {
  id: number
  timestamp: string
  actor: string
  event: string
  details: unknown
}

export interface Health {
  status: string
  database: { ok: boolean }
  payPal: {
    ok: boolean
    environment: string
    error: string | null
    features: { invoicing: boolean; transactionSearch: boolean } | null
  }
  llm: { provider: string; model: string; configured: boolean }
  auth?: { configured: boolean }
  shop?: { seeded: boolean; shopName: string }
}

export interface CurrentUser {
  role: 'seller' | 'customer'
  name: string
  email: string
}

export interface LedgerEntry {
  id: number
  type: 'Payment' | 'Refund'
  amount: number
  currency: string
  payPalTransactionId: string | null
  source: string
  recordedAt: string
  note: string | null
}

export interface OrderLine {
  sku: string
  name: string
  quantity: number
  unitPrice: number
  returnWindowDays: number
}

export interface ShopOrder {
  id: number
  orderNumber: string
  customerName: string
  customerEmail: string
  customerCountry: string
  status: string
  paymentMethod: string
  currency: string
  subtotal: number
  discount: number
  discountCode: string | null
  total: number
  paymentsRecorded: number
  refundsRecorded: number
  placedAt: string
  shippedAt: string | null
  deliveredAt: string | null
  payPalOrderId: string | null
  payPalCaptureId: string | null
  lines: OrderLine[]
  ledger: LedgerEntry[]
}

export type FindingSeverity = 'High' | 'Medium' | 'Low'

export interface Finding {
  id: number
  kind: string
  severity: FindingSeverity
  orderNumber: string | null
  payPalOrderId: string | null
  payPalTransactionId: string | null
  currency: string | null
  booksAmount: number | null
  payPalAmount: number | null
  difference: number | null
  message: string
  suggestedFix: string | null
}

export interface ReconciliationRun {
  id: number
  startedAt: string
  finishedAt: string | null
  status: 'Completed' | 'Failed'
  trigger: string
  ordersChecked: number
  ordersMatched: number
  payPalPaymentsChecked: number
  issuesFound: number
  note: string | null
  error: string | null
  findings: Finding[]
}

export interface ShopInfo {
  shopName: string
  sellerName: string
}

export interface Product {
  sku: string
  name: string
  price: number
  currency: string
  returnPolicy: string
}

export interface MyOrder {
  orderNumber: string
  placedAt: string
  status: string
  deliveredAt: string | null
  currency: string
  subtotal: number
  discount: number
  discountCode: string | null
  total: number
  refundedSoFar: number
  paidWith: string
  returnPolicy: string
  items: { sku: string; name: string; quantity: number; unitPrice: number }[]
}

export interface RefundOffer {
  orderNumber: string
  amount: number
  currency: string
  returnRequired: boolean
  reason: string | null
}

export interface ShopChatResponse {
  conversationId: string
  reply: string
  refundOffers: RefundOffer[]
  toolsUsed: string[]
}

export interface MyRefundRequest {
  id: string
  orderNumber: string | null
  amount: number | null
  currency: string | null
  status: ActionStatus
  createdAt: string
  decidedAt: string | null
}

export interface EvalToolCall {
  name: string
  arguments: unknown
  succeeded: boolean
  error: string | null
  result: string | null
}

export interface EvalCaseResult {
  id: string
  category: string
  status: 'PASS' | 'FAIL' | 'SKIP' | 'ERROR'
  failures: string[]
  prompts: string[]
  observation: {
    replies: string[]
    toolCalls: EvalToolCall[]
    proposals: { type: string; targetId: string; amount: number | null; currency: string | null; status: string; reason: string | null }[]
    offers?: { orderNumber: string; amount: number; currency: string; returnRequired: boolean }[]
    securityFlags: number
    writeAttempts: number
    executedActions: number
    finalReply: string
  } | null
  durationMs: number
  usage: { inputTokens: number; outputTokens: number; cacheReadInputTokens: number; cacheWriteInputTokens: number } | null
  model: string | null
  costUsd: number | null
}

export interface EvalReport {
  status?: 'running' | 'completed'
  info: { startedAt: string; provider: string; model: string; seedRunTag: string | null; totalCases: number }
  completed?: number
  score: { passed: number; scored: number; skipped: number }
  totals?: { costUsd: number | null; inputTokens: number; outputTokens: number; cacheReadTokens: number; cacheWriteTokens: number }
  results: EvalCaseResult[]
}

export interface EvalLatest {
  running: boolean
  lastExitCode: number | null
  output: string[]
  report: EvalReport | null
}

export interface EvalEstimate {
  cases: number
  byCategory: Record<string, number>
  model: string
  estimatedUsdPerCase: number | null
  estimatedUsd: number | null
  basis: string
}

export class ApiError extends Error {
  status: number

  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json', ...init?.headers },
  })
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`
    try {
      const problem = (await response.json()) as { title?: string }
      if (problem.title) message = problem.title
    } catch {
      // Not a problem-details body; keep the status text.
    }
    if (response.status === 401 && !problemTitleIsSpecific(message)) message = 'Please sign in.'
    if (response.status === 403) message = 'This account is not allowed to do that.'
    throw new ApiError(message, response.status)
  }
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

function problemTitleIsSpecific(message: string) {
  return !/^401\b/.test(message)
}

const post = <T>(path: string, body?: unknown) =>
  request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) })

export const api = {
  // Sign-in
  login: (email: string, password: string) => post<CurrentUser>('/api/auth/login', { email, password }),
  logout: () => post<void>('/api/auth/logout'),
  me: () => request<CurrentUser>('/api/auth/me'),

  // Seller back office
  chat: (message: string, conversationId: string | null) => post<ChatResponse>('/api/chat', { message, conversationId }),
  actions: (status?: ActionStatus) => request<PendingAction[]>(`/api/actions${status ? `?status=${status}` : ''}`),
  approve: (id: string) => post<PendingAction>(`/api/actions/${id}/approve`),
  reject: (id: string) => post<PendingAction>(`/api/actions/${id}/reject`),
  returnReceived: (id: string) => post<PendingAction>(`/api/actions/${id}/return-received`),
  audit: (take = 200) => request<AuditEntry[]>(`/api/audit?take=${take}`),
  orders: () => request<ShopOrder[]>('/api/orders'),
  runReconciliation: () => post<ReconciliationRun>('/api/reconciliation/runs'),
  latestReconciliation: () => request<ReconciliationRun | undefined>('/api/reconciliation/runs/latest'),

  // Eval (Development only; every run costs LLM tokens)
  evalLatest: () => request<EvalLatest>('/api/eval/latest'),
  evalEstimate: (category?: string) => request<EvalEstimate>(`/api/eval/estimate${category ? `?category=${encodeURIComponent(category)}` : ''}`),
  evalStart: (category?: string) => post<{ running: boolean }>('/api/eval/runs', { category: category ?? null }),
  evalCancel: () => post<void>('/api/eval/runs/cancel'),

  // Storefront
  shopInfo: () => request<ShopInfo>('/api/shop/info'),
  products: () => request<Product[]>('/api/shop/products'),
  myOrders: () => request<MyOrder[]>('/api/shop/orders'),
  shopChat: (message: string, conversationId: string | null) => post<ShopChatResponse>('/api/shop/chat', { message, conversationId }),
  requestRefund: (offer: RefundOffer) =>
    post<MyRefundRequest>('/api/shop/refund-requests', { orderNumber: offer.orderNumber, amount: offer.amount, reason: offer.reason }),
  myRefundRequests: () => request<MyRefundRequest[]>('/api/shop/refund-requests'),

  // Health answers 503 with a body when something is not configured, so read it either way.
  health: async (): Promise<Health> => {
    const response = await fetch('/api/health')
    return (await response.json()) as Health
  },
}

export function formatMoney(amount: number | null | undefined, currency: string | null | undefined): string {
  if (amount === null || amount === undefined || !currency) return '-'
  const decimals = ['JPY', 'HUF', 'TWD'].includes(currency) ? 0 : 2
  return `${currency} ${amount.toFixed(decimals)}`
}

export function formatTime(iso: string | null): string {
  if (!iso) return ''
  return new Date(iso).toLocaleString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
}

export function formatDate(iso: string | null): string {
  if (!iso) return ''
  return new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })
}
