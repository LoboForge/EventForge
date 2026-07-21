import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { Link, useRouter } from './router'
import { applyPageSeo } from './seo'
import {
  FALLBACK_MODELS,
  fetchAccount,
  fetchModels,
  formatCredits,
  getSessionToken,
  login,
  PublicApiError,
  setSessionToken,
  submitCapacityRequest,
  type AccountResponse,
  type PublicModel,
} from './publicApi'

function friendlyError(err: unknown): string {
  if (err instanceof PublicApiError) {
    if (err.code === 'http_401' || err.code === 'invalid_credentials') return 'Invalid email or password.'
    if (err.code === 'network_error') return err.message
    return err.message || 'Something went wrong. Please try again.'
  }
  return err instanceof Error ? err.message : 'Something went wrong. Please try again.'
}

function AuthShell({ children, wide }: { children: ReactNode; wide?: boolean }) {
  return (
    <div className="auth-page">
      <header className="landing-nav">
        <div className="landing-nav-inner">
          <Link to="/" className="landing-logo">EventForge</Link>
          <nav aria-label="Primary">
            <Link to="/">Home</Link>
            <a href="/#pricing">Starting packages</a>
            <a href="/ai-context/eventforge-context.md">API docs</a>
          </nav>
        </div>
      </header>
      <main className="auth-main">
        <div className={`auth-card${wide ? ' wide' : ''}`}>{children}</div>
      </main>
    </div>
  )
}

function ApiKeyPanel({ account }: { account: AccountResponse }) {
  const [copied, setCopied] = useState(false)
  if (!account.api_key) {
    return <p className="notice-info">Your API key will appear after ops confirms payment and activates capacity.</p>
  }
  return (
    <>
      <div className="api-key-box">
        <code>{account.api_key}</code>
        <button type="button" className="btn small" onClick={() => {
          void navigator.clipboard.writeText(account.api_key!).then(() => {
            setCopied(true)
            setTimeout(() => setCopied(false), 2000)
          })
        }}>{copied ? 'Copied!' : 'Copy'}</button>
      </div>
      <p className="key-warning">Keep this key secret. Treat it like a password and never commit it to source control.</p>
    </>
  )
}

function AccountView({ account, onRefresh }: { account: AccountResponse; onRefresh: () => void }) {
  const { navigate } = useRouter()
  return (
    <>
      <h1>Your account</h1>
      <p className="auth-sub">{account.email}{account.company ? ` · ${account.company}` : ''}</p>
      <p className="muted" style={{ margin: 0 }}>Credit balance</p>
      <p className="credit-balance">{formatCredits(account.credits)}</p>
      <p className="muted" style={{ marginTop: 18, marginBottom: 0 }}>API key</p>
      <ApiKeyPanel account={account} />
      <div className="row" style={{ marginTop: 22 }}>
        <Link to="/request" className="btn landing-btn-primary">Request more capacity</Link>
        <button type="button" className="btn secondary" onClick={onRefresh}>Refresh</button>
        <button type="button" className="btn secondary" onClick={() => {
          setSessionToken('')
          navigate('/login')
        }}>Sign out</button>
      </div>
    </>
  )
}

export function LoginPage() {
  const [account, setAccount] = useState<AccountResponse | null>(null)
  const [loading, setLoading] = useState(() => Boolean(getSessionToken()))
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => applyPageSeo({
    title: 'Sign in — EventForge',
    description: 'Sign in to view your EventForge account, balance, and API key.',
    canonicalPath: '/login',
  }), [])

  const loadAccount = () => {
    if (!getSessionToken()) {
      setLoading(false)
      setAccount(null)
      return
    }
    setLoading(true)
    fetchAccount().then(setAccount).catch(() => {
      setSessionToken('')
      setAccount(null)
    }).finally(() => setLoading(false))
  }
  useEffect(loadAccount, [])

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true); setError('')
    try {
      await login(email.trim(), password)
      loadAccount()
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setBusy(false)
    }
  }

  if (loading) return <AuthShell><div className="capture-spinner">Loading your account…</div></AuthShell>
  if (account) return <AuthShell wide><AccountView account={account} onRefresh={loadAccount} /></AuthShell>
  return (
    <AuthShell>
      <h1>Sign in</h1>
      <p className="auth-sub">View your credit balance and API key.</p>
      {error && <div className="error" role="alert">{error}</div>}
      <form onSubmit={submit}>
        <label className="auth-field"><span>Email</span><input type="email" required autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} /></label>
        <label className="auth-field"><span>Password</span><input type="password" required autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} /></label>
        <button className="btn landing-btn-primary auth-submit" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
      </form>
      <p className="auth-alt">Need capacity? <Link to="/request">Submit a request</Link>.</p>
    </AuthShell>
  )
}

export function SignupPage() {
  const query = new URLSearchParams(window.location.search)
  const plan = query.get('plan')
  const [models, setModels] = useState<PublicModel[]>(FALLBACK_MODELS)
  const [selected, setSelected] = useState<string[]>([])
  const [email, setEmail] = useState('')
  const [company, setCompany] = useState('')
  const [name, setName] = useState('')
  const [estimatedJobs, setEstimatedJobs] = useState(plan === 'starter' ? 1000 : plan === 'pro' ? 4000 : plan === 'scale' ? 14000 : 1000)
  const [notes, setNotes] = useState(plan ? `Interested in the ${plan} starting package.` : '')
  const [preferredPayment, setPreferredPayment] = useState<'paypal' | 'wire' | 'monero' | 'any'>('any')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [requestId, setRequestId] = useState('')

  useEffect(() => applyPageSeo({
    title: 'Request capacity — EventForge',
    description: 'Tell us which models and approximate job volume you need. Settle by PayPal invoice, wire transfer, or Monero.',
    canonicalPath: '/request',
  }), [])
  useEffect(() => { void fetchModels().then((data) => setModels(data.models)) }, [])

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (selected.length === 0) {
      setError('Select at least one model.')
      return
    }
    setBusy(true); setError('')
    try {
      const response = await submitCapacityRequest({
        email: email.trim(),
        company: company.trim() || undefined,
        name: name.trim() || undefined,
        models: selected,
        estimated_jobs: estimatedJobs,
        notes: notes.trim() || undefined,
        preferred_payment: preferredPayment,
      })
      setRequestId(response.request_id)
    } catch (err) {
      setError(friendlyError(err))
    } finally {
      setBusy(false)
    }
  }

  if (requestId) {
    return (
      <AuthShell wide>
        <p className="landing-eyebrow">Request received</p>
        <h1>We’ll follow up with payment instructions.</h1>
        <p className="auth-sub">Ops will review your model and volume needs, then email a PayPal invoice, wire details, or Monero payment instructions.</p>
        <p className="notice-info">Reference: <code>{requestId}</code></p>
        <p className="landing-copy">After payment clears, ops will activate your account and API key manually.</p>
        <div className="row"><Link to="/" className="btn landing-btn-primary">Back to EventForge</Link><Link to="/login" className="btn secondary">Sign in</Link></div>
      </AuthShell>
    )
  }

  return (
    <AuthShell wide>
      <p className="landing-eyebrow">Manual onboarding</p>
      <h1>Request capacity</h1>
      <p className="auth-sub">Tell us what you plan to run. We’ll confirm availability and email payment instructions.</p>
      {error && <div className="error" role="alert">{error}</div>}
      <form onSubmit={submit}>
        <div className="request-form-grid">
          <label className="auth-field"><span>Email</span><input type="email" required autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} /></label>
          <label className="auth-field"><span>Name <small>(optional)</small></span><input value={name} autoComplete="name" onChange={(e) => setName(e.target.value)} /></label>
          <label className="auth-field"><span>Company <small>(optional)</small></span><input value={company} autoComplete="organization" onChange={(e) => setCompany(e.target.value)} /></label>
          <label className="auth-field"><span>Estimated jobs</span><input type="number" required min={1} value={estimatedJobs} onChange={(e) => setEstimatedJobs(Number(e.target.value))} /></label>
        </div>
        <fieldset className="model-request-fieldset">
          <legend>Models</legend>
          <div className="model-request-grid">
            {models.map((model) => (
              <label key={model.id} className={`model-request-option${selected.includes(model.id) ? ' selected' : ''}`}>
                <input type="checkbox" checked={selected.includes(model.id)} onChange={(e) => {
                  setSelected((current) => e.target.checked ? [...current, model.id] : current.filter((id) => id !== model.id))
                }} />
                <span><strong>{model.name}</strong><small>{model.kind}</small></span>
              </label>
            ))}
          </div>
        </fieldset>
        <label className="auth-field"><span>Preferred payment</span>
          <select value={preferredPayment} onChange={(e) => setPreferredPayment(e.target.value as typeof preferredPayment)}>
            <option value="any">Any / discuss with ops</option>
            <option value="paypal">PayPal invoice</option>
            <option value="wire">Wire transfer</option>
            <option value="monero">Monero (XMR)</option>
          </select>
        </label>
        <label className="auth-field"><span>Notes <small>(optional)</small></span><textarea rows={4} value={notes} onChange={(e) => setNotes(e.target.value)} placeholder="Workload, timing, custom LoRAs, or other requirements" /></label>
        <button className="btn landing-btn-primary auth-submit" disabled={busy}>{busy ? 'Submitting…' : 'Submit capacity request'}</button>
      </form>
      <p className="auth-alt">No charge is made here. Payment is arranged after ops reviews your request.</p>
    </AuthShell>
  )
}
