/** Full-page loading state shown while the app finds out who is signed in. */
export function AppLoader({ label = 'Starting SellerPilot…' }: { label?: string }) {
  return (
    <main className="app-loader" role="status" aria-live="polite">
      <div className="loader-mark" aria-hidden>
        <span className="loader-ring" />
        <span className="loader-plane">✈</span>
      </div>
      <div className="loader-label">{label}</div>
      <div className="loader-dots" aria-hidden>
        <span />
        <span />
        <span />
      </div>
    </main>
  )
}
