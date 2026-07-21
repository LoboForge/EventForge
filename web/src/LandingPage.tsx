import { useEffect, useState } from 'react'
import { Link } from './router'
import { applyPageSeo, buildLandingJsonLd, SITE } from './seo'
import {
  ENTERPRISE_CONTACT,
  FALLBACK_MODELS,
  FALLBACK_PLANS,
  fetchModels,
  fetchPlans,
  formatCredits,
  type PublicModel,
  type PublicPlan,
} from './publicApi'

const KIND_LABELS: Record<string, string> = {
  video: 'Video',
  image: 'Image',
  'image-edit': 'Image editing',
  music: 'Music',
  captioning: 'Captioning',
  text: 'Text / LLM',
}

export const LANDING_FAQ = [
  {
    q: 'How do credits and billing work?',
    a: 'Submit a capacity request with your models and approximate job volume. After review, ops sends a PayPal invoice, wire instructions, or Monero payment details. Once payment clears, ops activates your API key and capacity.',
  },
  {
    q: 'How do I get an API key?',
    a: 'Submit the form at /request or call POST /v1/public/capacity-request. Ops reviews the request, arranges payment, and activates an efk_ API key after payment clears.',
  },
  {
    q: 'Can I use my own fine-tuned models or LoRAs?',
    a: 'Yes — custom LoRAs are a first-class feature. Upload your LoRA weights with POST /v1/assets/loras, then reference them in your job payload. EventForge routes each job only to workers that have your LoRAs available, so results are consistent every time. All image and video models support custom LoRAs.',
  },
  {
    q: 'How do I receive results?',
    a: 'Two ways: subscribe to WSS /v1/ws for live job lifecycle events (started, completed, failed) with replay support after downtime, or poll GET /api/v1/events?since=… over plain HTTP. Completed jobs include artifact URLs served from EventForge storage.',
  },
  {
    q: 'What about SLAs and priority?',
    a: 'The queue has four priority tiers — admin, vip, normal, and bulk — so latency-sensitive jobs jump ahead of batch work. Capacity scales with your workload. For contractual SLAs, dedicated capacity, or invoicing, contact sales@loboforge.com for an Enterprise agreement.',
  },
  {
    q: 'Do credits expire?',
    a: 'No. Credits stay on your account until you use them. If a job fails because of an infrastructure problem on our side, the credits are not consumed.',
  },
] as const

const CURL_EXAMPLE = `curl -X POST https://eventforge.loboforge.com/v1/jobs \\
  -H "Authorization: Bearer $EVENTFORGE_API_KEY" \\
  -H "Content-Type: application/json" \\
  -d '{
    "capability": "image",
    "tier": "normal",
    "payload": {
      "model": "flux-2-klein",
      "prompt": "product photo of a titanium watch, studio lighting",
      "loras": [{ "name": "my-brand-style.safetensors", "strength": 0.8 }]
    }
  }'`

export function LandingPage() {
  const [plans, setPlans] = useState<PublicPlan[]>(FALLBACK_PLANS)
  const [models, setModels] = useState<PublicModel[]>(FALLBACK_MODELS)
  const [enterpriseContact, setEnterpriseContact] = useState(ENTERPRISE_CONTACT)

  useEffect(() => {
    let cancelled = false
    fetchPlans().then((data) => {
      if (cancelled) return
      setPlans(data.plans)
      if (data.enterprise_contact) setEnterpriseContact(data.enterprise_contact)
    })
    fetchModels().then((data) => {
      if (!cancelled) setModels(data.models)
    })
    return () => {
      cancelled = true
    }
  }, [])

  useEffect(() => {
    return applyPageSeo({
      title: SITE.title,
      description: SITE.description,
      canonicalPath: '/',
      jsonLd: buildLandingJsonLd(
        plans.map((p) => ({ name: p.name, price_usd: p.price_usd, credits: p.credits, description: p.description })),
        LANDING_FAQ.map((f) => ({ question: f.q, answer: f.a })),
      ),
    })
  }, [plans])

  return (
    <div className="landing">
      <header className="landing-nav">
        <div className="landing-nav-inner">
          <Link to="/" className="landing-logo">EventForge</Link>
          <nav aria-label="Primary">
            <a href="#models">Models</a>
            <a href="#loras">Custom LoRAs</a>
            <a href="#pricing">Pricing</a>
            <a href="#developers">Developers</a>
            <a href="/ai-context/eventforge-context.md">API docs</a>
            <Link to="/login">Sign in</Link>
            <Link to="/request" className="landing-nav-cta">Request capacity</Link>
          </nav>
        </div>
      </header>

      <main>
        <section className="landing-hero">
          <p className="landing-eyebrow">Production inference · Capacity matched to your workload</p>
          <h1>
            Request production GPU capacity.
            <br />
            <span className="hero-gradient">Ship AI features today.</span>
          </h1>
          <p className="landing-lead">
            EventForge sells GPU inference as a service: image, video, music, and text models behind one
            production job queue. Tell us which models and approximate volume you need; after payment clears,
            we activate your API key — with <strong>your own custom LoRAs</strong> if you have them.
          </p>
          <div className="landing-actions">
            <Link className="btn landing-btn-primary landing-btn-lg" to="/request">
              Request capacity
            </Link>
            <a className="btn secondary landing-btn-lg" href="/ai-context/eventforge-context.md">
              Read the API docs
            </a>
          </div>
          <ul className="landing-trust" aria-label="Highlights">
            <li>Plans from $29</li>
            <li>REST in, WebSocket out</li>
            <li>Bring your own LoRAs</li>
            <li>Priority queue tiers</li>
            <li>Artifact storage included</li>
          </ul>
        </section>

        <section id="how-it-works" className="landing-section">
          <h2>How it works</h2>
          <p className="landing-copy">
            One integration, every model. You never touch a GPU box, a CUDA image, or a scaling policy —
            capacity scales with your workload.
          </p>
          <div className="landing-steps">
            <article className="landing-step">
              <span className="step-num">1</span>
              <h3>Enqueue a job</h3>
              <p>
                <code>POST /v1/jobs</code> with your API key. Pick a model, a priority tier
                (admin / vip / normal / bulk), and an arbitrary payload.
              </p>
            </article>
            <article className="landing-step">
              <span className="step-num">2</span>
              <h3>GPUs run it</h3>
              <p>
                The EventForge fleet claims the job, loads any custom LoRAs it needs, executes,
                and uploads artifacts to managed storage.
              </p>
            </article>
            <article className="landing-step">
              <span className="step-num">3</span>
              <h3>Results stream back</h3>
              <p>
                Live events over <code>WSS /v1/ws</code> — started, completed, failed — with replay
                after downtime, or plain HTTP polling if you prefer.
              </p>
            </article>
          </div>
        </section>

        <section id="models" className="landing-section">
          <h2>Model catalog</h2>
          <p className="landing-copy">
            Every model below is live on the platform and callable through the same job API.
            Models marked <span className="lora-chip">LoRA</span> accept your custom fine-tunes.
          </p>
          <div className="landing-grid model-grid">
            {models.map((m) => (
              <article key={m.id} className="landing-card model-card">
                <div className="model-card-head">
                  <span className={`model-kind model-kind-${m.kind.replace(/[^a-z]/g, '')}`}>
                    {KIND_LABELS[m.kind] ?? m.kind}
                  </span>
                  {m.supports_custom_loras && <span className="lora-chip" title="Supports custom LoRAs">LoRA</span>}
                </div>
                <h3>{m.name}</h3>
                <p>{m.description}</p>
              </article>
            ))}
          </div>
        </section>

        <section id="loras" className="landing-section landing-section-muted landing-lora-spotlight">
          <div className="lora-spotlight-inner">
            <div className="lora-spotlight-copy">
              <p className="landing-eyebrow">Headline feature</p>
              <h2>Bring your own fine-tunes</h2>
              <p className="landing-copy">
                Your style, your characters, your product — not stock model output. Upload LoRA weights once
                via <code>POST /v1/assets/loras</code> and reference them in any job. EventForge routes each
                job <strong>only to workers that have your LoRAs loaded</strong>, so you get consistent,
                on-brand results without managing a single GPU.
              </p>
              <ul className="lora-points">
                <li>Works with every image and video model in the catalog</li>
                <li>Private to your account — never shared across tenants</li>
                <li>Stack multiple LoRAs with per-LoRA strength control</li>
                <li>Upload via API: automate it from CI or an agent</li>
              </ul>
              <Link to="/request" className="btn landing-btn-primary">Request LoRA-ready capacity</Link>
            </div>
            <pre className="lora-code" aria-label="LoRA upload example">
              <code>{`# 1. Upload your fine-tune once
curl -X POST https://eventforge.loboforge.com/v1/assets/loras \\
  -H "Authorization: Bearer $EVENTFORGE_API_KEY" \\
  -F "file=@my-brand-style.safetensors"

# 2. Use it in any image/video job
"loras": [
  { "name": "my-brand-style.safetensors",
    "strength": 0.8 }
]`}</code>
            </pre>
          </div>
        </section>

        <section id="pricing" className="landing-section">
          <h2>Starting packages</h2>
          <p className="landing-copy">
            Typical prepaid packages provide useful price anchors; final capacity is confirmed after review.
            Settle by PayPal invoice, wire transfer, or Monero. Ops activates access after payment clears.
          </p>
          <div className="pricing-grid">
            {plans.map((p, i) => (
              <article key={p.id} className={`pricing-card${i === 1 ? ' pricing-featured' : ''}`}>
                {i === 1 && <span className="pricing-flag">Most popular</span>}
                <h3>{p.name}</h3>
                <p className="pricing-price">
                  <span className="pricing-amount">${p.price_usd}</span>
                  <span className="pricing-per"> / {formatCredits(p.credits)} credits</span>
                </p>
                <p className="pricing-desc">{p.description}</p>
                <ul className="pricing-features">
                  {p.features.map((f) => (
                    <li key={f}>{f}</li>
                  ))}
                </ul>
                <Link to={`/request?plan=${encodeURIComponent(p.id)}`} className="btn landing-btn-primary pricing-cta">
                  Request {p.name}
                </Link>
              </article>
            ))}
            <article className="pricing-card pricing-enterprise">
              <h3>Enterprise</h3>
              <p className="pricing-price">
                <span className="pricing-amount">Custom</span>
              </p>
              <p className="pricing-desc">
                Contractual SLAs, dedicated capacity, invoicing, and custom model deployments.
              </p>
              <ul className="pricing-features">
                <li>Everything in Scale</li>
                <li>Contractual SLAs</li>
                <li>Dedicated capacity</li>
                <li>Custom model onboarding</li>
              </ul>
              <a href={`mailto:${enterpriseContact}?subject=EventForge%20Enterprise`} className="btn secondary pricing-cta">
                Contact sales
              </a>
            </article>
          </div>
        </section>

        <section id="developers" className="landing-section">
          <h2>Built for developers — and for AI agents</h2>
          <p className="landing-copy">
            Everything is plain HTTPS + JSON. Capacity requests, job submission, and LoRA upload are API
            endpoints. Payment settlement and activation are reviewed by ops before access is enabled.
          </p>
          <pre className="dev-code" aria-label="curl example for POST /v1/jobs">
            <code>{CURL_EXAMPLE}</code>
          </pre>
          <div className="landing-grid dev-grid">
            <article className="landing-card">
              <h3>Queue with priority tiers</h3>
              <p>
                Four tiers — admin, vip, normal, bulk — so interactive requests beat batch renders.
                Leases, retries, and failure events are built in.
              </p>
            </article>
            <article className="landing-card">
              <h3>Events, not polling loops</h3>
              <p>
                <code>WSS /v1/ws</code> pushes <code>forge.job.completed</code> and friends in real time,
                with replay via <code>GET /api/v1/events?since=…</code> after downtime.
              </p>
            </article>
            <article className="landing-card">
              <h3>Agent-friendly onboarding</h3>
              <p>
                <code>POST /v1/public/capacity-request</code> → ops review → payment instructions → API key.
                Machine-readable docs at <a href="/llms.txt">/llms.txt</a> and{' '}
                <a href="/ai-context/eventforge-context.md">/ai-context</a>.
              </p>
            </article>
          </div>
        </section>

        <section id="faq" className="landing-section">
          <h2>Frequently asked questions</h2>
          <div className="faq-list">
            {LANDING_FAQ.map((f) => (
              <details key={f.q} className="faq-item">
                <summary>{f.q}</summary>
                <p>{f.a}</p>
              </details>
            ))}
          </div>
        </section>

        <section className="landing-section landing-final-cta">
          <h2>Tell us what you need</h2>
          <p className="landing-copy centered">
            Submit your models and approximate volume. We’ll confirm capacity and payment instructions.
          </p>
          <div className="landing-actions">
            <Link className="btn landing-btn-primary landing-btn-lg" to="/request">Request capacity</Link>
            <a className="btn secondary landing-btn-lg" href={`mailto:${enterpriseContact}?subject=EventForge%20Enterprise`}>
              Talk to sales
            </a>
          </div>
        </section>

        <noscript>
          <section className="landing-section">
            <h2>EventForge summary</h2>
            <p>
              EventForge provides GPU inference capacity as a service. Starting packages begin at $29. Request
              the models and approximate volume you need, settle by PayPal invoice, wire, or Monero, and receive an API key to submit
              image, video, music, and text generation jobs to a production GPU queue with priority tiers.
              Custom LoRA fine-tunes are supported on all image and video models via POST /v1/assets/loras.
              Results arrive over WebSocket (WSS /v1/ws) or HTTP polling. Request access at /request or
              programmatically via POST /v1/public/capacity-request.
            </p>
            <p>
              Documentation: <a href="/ai-context/eventforge-context.md">/ai-context/eventforge-context.md</a>.
              Machine-readable summary: <a href="/llms.txt">/llms.txt</a>. Enterprise: {ENTERPRISE_CONTACT}.
            </p>
          </section>
        </noscript>
      </main>

      <footer className="landing-footer">
        <div className="footer-cols">
          <div>
            <p className="footer-brand"><strong>EventForge</strong></p>
            <p className="muted">GPU inference capacity as a service, by <a href="https://www.loboforge.com/">LoboForge</a>.</p>
          </div>
          <div>
            <p className="footer-head">Product</p>
            <a href="#models">Models</a>
            <a href="#pricing">Pricing</a>
            <a href="#loras">Custom LoRAs</a>
            <Link to="/request">Request capacity</Link>
            <Link to="/login">Sign in</Link>
          </div>
          <div>
            <p className="footer-head">Developers</p>
            <a href="/ai-context/eventforge-context.md">API docs</a>
            <a href="/ai-context/agents.md">Agent runbook</a>
            <a href="/llms.txt">llms.txt</a>
            <a href="/sitemap.xml">Sitemap</a>
          </div>
          <div>
            <p className="footer-head">Contact</p>
            <a href={`mailto:${enterpriseContact}`}>{enterpriseContact}</a>
            <a href="mailto:ops@loboforge.com">ops@loboforge.com</a>
          </div>
        </div>
      </footer>
    </div>
  )
}
