// Typed client for the SellerPilot API. All money values are shown as given by the server.

export type ActionType = 'Refund' | 'InvoiceReminder'
export type ActionStatus = 'Pending' | 'Approved' | 'Rejected' | 'Executed' | 'Failed'

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
}

export interface ReminderDetails {
  subject?: string | null
  note?: string
  invoiceNumber?: string
  customerName?: string | null
  dueDate?: string | null
  daysOverdue?: number
}

export interface PendingAction {
  id: string
  type: ActionType
  targetId: string
  amount: number | null
  currency: string | null
  reason: string
  summary: string
  details: (RefundDetails & ReminderDetails) | null
  status: ActionStatus
  payPalResultId: string | null
  error: string | null
  createdAt: string
  decidedAt: string | null
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
    throw new ApiError(message, response.status)
  }
  return (await response.json()) as T
}

export const api = {
  chat: (message: string, conversationId: string | null) =>
    request<ChatResponse>('/api/chat', {
      method: 'POST',
      body: JSON.stringify({ message, conversationId }),
    }),

  actions: (status?: ActionStatus) =>
    request<PendingAction[]>(`/api/actions${status ? `?status=${status}` : ''}`),

  approve: (id: string) => request<PendingAction>(`/api/actions/${id}/approve`, { method: 'POST' }),

  reject: (id: string) => request<PendingAction>(`/api/actions/${id}/reject`, { method: 'POST' }),

  audit: (take = 200) => request<AuditEntry[]>(`/api/audit?take=${take}`),

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
