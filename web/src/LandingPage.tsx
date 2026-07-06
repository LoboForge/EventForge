import { useEffect } from 'react'
import { Link } from './router'
import { applyPageSeo, buildLandingJsonLd, SITE } from './seo'

const FEATURES = [
  {
    title: 'No boxes to provision',
    body: 'Skip Vast.ai contracts, CUDA images, bootstrap scripts, and SSH babysitting. EventForge owns the fleet end to end.',
  },
  {
    title: 'No fleet management',
    body: 'Workers check in, claim jobs, and report health automatically. You integrate once with HTTP + WebSocket — not per GPU.',
  },
  {
    title: 'No manual scaling',
    body: 'Queue depth drives capacity. Burst image, video, or LLM workloads without capacity planning spreadsheets.',
  },
  {
    title: 'Pay per job',
    body: 'Usage-based GPU compute: enqueue work, receive completion events, fetch artifacts. Idle fleet is our problem, not yours.',
  },
] as const

const FLOW = [
  'POST /v1/jobs — enqueue with your API key',
  'Workers claim matching capability + tier',
  'WSS /v1/ws — live started / completed / failed events',
  'GET /api/v1/events — replay when offline',
  'Artifacts delivered through EventForge storage',
] as const

export function LandingPage() {
  useEffect(() => {
    return applyPageSeo({
      title: SITE.title,
      description: SITE.description,
      canonicalPath: '/',
      jsonLd: buildLandingJsonLd(),
    })
  }, [])

  return (
    <div className="landing">
      <header className="landing-nav">
        <div className="landing-nav-inner">
          <span className="landing-logo">EventForge</span>
          <nav aria-label="Primary">
            <a href="#features">Features</a>
            <a href="#how-it-works">How it works</a>
            <a href="/ai-context/eventforge-context.md">Integration</a>
            <Link to="/ops" className="landing-nav-cta">Ops console</Link>
          </nav>
        </div>
      </header>

      <main>
        <section className="landing-hero">
          <p className="landing-eyebrow">Production ready</p>
          <h1>GPU fleet as a service</h1>
          <p className="landing-lead">
            No provisioning your own boxes. No managing the fleet. No manual scaling.
            Submit jobs, subscribe to events, and pay per job — EventForge runs the GPUs.
          </p>
          <div className="landing-actions">
            <a className="btn landing-btn-primary" href="mailto:ops@loboforge.com?subject=EventForge%20integration">
              Request access
            </a>
            <a className="btn secondary" href="/ai-context/eventforge-context.md">
              Read integration guide
            </a>
          </div>
          <ul className="landing-trust" aria-label="Highlights">
            <li>HTTP job queue</li>
            <li>WebSocket event bus</li>
            <li>Multi-tenant API keys</li>
            <li>Image, video &amp; LLM workers</li>
          </ul>
        </section>

        <section id="features" className="landing-section">
          <h2>What you do not run</h2>
          <div className="landing-grid">
            {FEATURES.map((f) => (
              <article key={f.title} className="landing-card">
                <h3>{f.title}</h3>
                <p>{f.body}</p>
              </article>
            ))}
          </div>
        </section>

        <section id="how-it-works" className="landing-section">
          <h2>How integrators connect</h2>
          <p className="landing-copy">
            White-labeled apps receive an API key mapped to an <code>app_id</code>. Enqueue GPU work over HTTPS,
            listen for lifecycle events over WebSocket, and replay missed events after downtime. Workers never
            touch your credentials — they use a separate worker key on EventForge-managed machines.
          </p>
          <ol className="landing-flow">
            {FLOW.map((step) => (
              <li key={step}>{step}</li>
            ))}
          </ol>
        </section>

        <section className="landing-section landing-section-muted">
          <h2>For operators</h2>
          <p className="landing-copy">
            Fleet health, queue depth, failures, and Vast.ai rent/terminate live in the authenticated ops console.
            That dashboard is separate from this public page.
          </p>
          <Link to="/ops" className="btn">Open ops console</Link>
        </section>

        <noscript>
          <section className="landing-section">
            <h2>EventForge summary</h2>
            <p>
              EventForge is a production GPU fleet platform: HTTP job queue, WebSocket event bus, worker check-in,
              artifact storage, and ops tooling. Integrators pay per job without provisioning or scaling their own GPU boxes.
            </p>
            <p>
              Documentation: <a href="/ai-context/eventforge-context.md">/ai-context/eventforge-context.md</a>.
              Machine-readable summary: <a href="/llms.txt">/llms.txt</a>.
            </p>
          </section>
        </noscript>
      </main>

      <footer className="landing-footer">
        <p>
          <strong>EventForge</strong> — GPU fleet provider by{' '}
          <a href="https://www.loboforge.com/">LoboForge</a>.
        </p>
        <p className="muted">
          <a href="/ai-context/agents.md">Agent runbook</a> · <a href="/llms.txt">llms.txt</a> · <a href="/sitemap.xml">sitemap</a> ·{' '}
          <a href="/robots.txt">robots.txt</a>
        </p>
      </footer>
    </div>
  )
}
