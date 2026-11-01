# SellerPilot: AI payment ops for small online sellers

**Chat with your PayPal account. The AI proposes; you approve.**

SellerPilot helps a one-person online shop (think a seller of handmade scarves and home textiles with buyers in the US and Singapore) handle PayPal admin by chatting: *"Did this customer pay?"*, *"Which invoices are still unpaid?"*, *"This customer wants a refund. What should I do?"*. The assistant looks up real PayPal data, explains it, and **proposes** refunds or invoice reminders. Nothing moves until the seller clicks **Approve**.

> Built for the PayPal AI Hackathon 2026. **PayPal sandbox only.**
>
> Demo video: _TODO: link_ · Eval score: _TODO: X/30_

_TODO: screenshot (chat + approval card)_

## The problem

Small sellers who sell overseas do their payment admin alone. They dig through dashboards to check payments, chase unpaid invoices by hand, and decide on refunds without a finance team. It is slow, and mistakes cost money.

## What it does

| | Feature | PayPal API |
|---|---|---|
| 🔎 | Look up an order or payment by ID, or find a customer's payments | Orders v2, Payments v2, Transaction Search |
| 🧾 | List unpaid and overdue invoices with days overdue | Invoicing v2 |
| ✉️ | Propose polite payment reminders; send them after approval | Invoicing v2 `remind` |
| ↩️ | Propose full or partial refunds; execute them after approval | Payments v2 `captures/{id}/refund` |
| ✅ | Human approval for every write action, with an "approve all" for batches | SellerPilot |
| 📜 | Audit log of every tool call, proposal, guardrail block, approval and execution | SellerPilot |

## How it works

```mermaid
flowchart LR
    UI[React chat UI<br/>chat · approval cards · audit log] -->|REST| API
    subgraph API[ASP.NET Core API]
        Chat[ChatController] --> Agent[AgentService<br/>tool-calling loop, max 6 rounds]
        Agent --> LLM[ILlmClient<br/>Claude or Gemini]
        Agent --> Tools[Read tools + Propose tools]
        Tools -->|read only| PPR[IPayPalReadClient]
        Tools -->|create| PA[(PendingAction)]
        Appr[ApprovalController] --> AS[ApprovalService<br/>claim → re-validate → execute]
        AS --> PPW[IPayPalWriteClient]
        AS --> PA
        Agent & AS --> Audit[(AuditLog)]
    end
    PPR & PPW --> PayPal[PayPal sandbox]
```

**Agent loop.** The seller's message, the system prompt and the tool definitions go to the LLM. The LLM answers or asks for tools (`GetOrder`, `SearchTransactions`, `ListUnpaidInvoices`, `ProposeRefund`, `ProposeInvoiceReminder`). SellerPilot validates every argument, runs the tool, and returns the result as data. This repeats until there is a final answer (at most 6 tool rounds).

**Approval flow.**

```text
LLM ─► ProposeRefund(capture_id, amount, reason)
        └► guardrails: capture exists · not already refunded · amount ≤ captured − refunded − pending · amount ≤ limit
        └► PendingAction {Pending}  ──► approval card in the UI
Seller clicks Approve ─► POST /api/actions/{id}/approve
        └► atomic claim (Pending → Approved; a second click gets 409)
        └► re-validate against live PayPal state (it may have changed)
        └► PayPal refund with PayPal-Request-Id = PendingAction.Id (idempotent retries)
        └► Executed / Failed ─► audit log
```

## Safety design

The core rule: **the AI can only propose.** Even a fully manipulated model cannot move money.

| Risk | Protection |
|---|---|
| AI moves money by itself | Tools only get `IPayPalReadClient`; only `ApprovalService` (behind the approve endpoint) has `IPayPalWriteClient`. Enforced by the compiler and an architecture test. |
| Prompt injection in customer notes, item names or memos | Suspicious text is **withheld** from the LLM (placeholder + security notice), flagged in the UI and audited; the system prompt treats tool results as data; limits are enforced in code. |
| Over-refund | Amount ≤ captured − already refunded − proposals awaiting approval, and ≤ a per-currency limit (e.g. USD 500); currencies without a limit can't be refunded. |
| Double refund on retry or double click | `PayPal-Request-Id` = action ID on every write; atomic `Pending → Approved` claim; retries reuse the same key. |
| Stale approval | Every approval re-checks live PayPal state (refunded in the meantime? invoice paid?). |
| Hallucinated or malicious IDs | IDs must be plain letters/digits and are checked against PayPal before a proposal exists. |
| Sensitive data leak | Emails masked, no addresses or payer IDs sent to the LLM; secrets never logged. |
| Agent loops | Max 6 tool rounds, max 10 calls per round, timeouts on every HTTP call. |
| Live payments by mistake | The PayPal base URL must be the sandbox host or the app won't start. |

More detail and trade-offs: [DECISIONS.md](DECISIONS.md).

## Evaluation

`tools/SellerPilot.Eval` runs 30 prompts ([eval/cases.json](eval/cases.json)) through the real agent with real sandbox data and scores them with deterministic checks: which tools were called, which proposals were created (type, target, amount), what the reply says, and whether an injection flag was raised. During an eval the PayPal write client is replaced by one that refuses every call, so any attempt to move money fails the case.

| Category | Cases | What is checked |
|---|---|---|
| Lookups | 8 | Right tool, right facts in the answer |
| Invoices and reminders | 6 | Correct unpaid invoices, one reminder proposal per invoice |
| Refunds | 8 | Correct amount, over-refunds blocked, "already refunded" detected |
| Prompt injection and abuse | 5 | Flagged, refused, no unsafe proposal |
| Unknown or out of scope | 3 | No invented data |

Score: _TODO_ (results go to `eval/results/latest.md`; `history.csv` keeps every run for before/after comparisons).

## Tech stack

ASP.NET Core 10 (C#) · EF Core + SQLite · PayPal REST APIs (sandbox) · Claude (official Anthropic C# SDK, `claude-opus-5-5`) or Gemini · React + Vite + TypeScript · xUnit + NSubstitute

## Run it locally

**Prerequisites:** .NET 10 SDK, Node.js 20+, a [PayPal developer](https://developer.paypal.com/) account, and a Claude or Gemini API key.

1. **Create a sandbox app.** In the PayPal developer dashboard go to *Apps & Credentials → Sandbox → Create App*. Copy the client ID and secret. In the app's features, make sure **Invoicing** and **Transaction search** are enabled.

2. **Store the secrets** (never in source control):

   ```bash
   dotnet user-secrets set "PayPal:ClientId" "<sandbox client id>" --project src/SellerPilot.Api
   dotnet user-secrets set "PayPal:ClientSecret" "<sandbox secret>" --project src/SellerPilot.Api
   dotnet user-secrets set "Llm:Claude:ApiKey" "<anthropic api key>" --project src/SellerPilot.Api
   # or, for Gemini:
   # dotnet user-secrets set "Llm:Provider" "Gemini" --project src/SellerPilot.Api
   # dotnet user-secrets set "Llm:Gemini:ApiKey" "<gemini api key>" --project src/SellerPilot.Api
   ```

   The seed and eval tools share these secrets. You can use a `.env` file in the repo root instead (see [.env.example](.env.example)).

3. **Create demo data in the sandbox:**

   ```bash
   dotnet run --project tools/SellerPilot.Seed -- check   # verifies credentials and enabled features
   dotnet run --project tools/SellerPilot.Seed            # creates orders, refunds and invoices → seed-output.json
   ```

   If the sandbox app can't process cards, the seed prints approval links instead. Open each one, log in with the sandbox **personal** account, approve, and then run `dotnet run --project tools/SellerPilot.Seed -- capture`. Transaction Search can take a few hours to show new payments.

4. **Start the API and the UI:**

   ```bash
   dotnet run --project src/SellerPilot.Api      # http://localhost:5080 (check /api/health)
   cd web && npm install && npm run dev          # http://localhost:5173
   ```

## Tests

```bash
dotnet test                                     # unit + integration tests; PayPal and the LLM are mocked
dotnet run --project tools/SellerPilot.Eval     # live eval: --case refund-01,inject-01 · --category refunds · --delay-ms 2000
```

## Limitations and what's next

- Single demo seller: no user accounts or authentication on the approval endpoint (needed before any real use).
- Chat history is in memory and is lost on restart; the audit log and proposals persist in SQLite.
- Prompt-injection detection is pattern-based and can hide harmless notes; the hard limits don't depend on it.
- Next: dispute summaries with suggested replies, multi-currency explanations (MYR/USD/SGD), PayPal webhooks for status changes, per-merchant limits, monitoring of agent errors.

## Built with AI-assisted development

Built with Claude Code as a pair programmer: plan first, small slices, tests for every service. All code was reviewed and tested before it was committed.
