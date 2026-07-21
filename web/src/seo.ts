export const SITE = {
  name: 'EventForge',
  title: 'EventForge — Request Production GPU Inference Capacity',
  description:
    'Request production capacity for image, video, music, and text generation behind one job queue API. Bring custom LoRAs and settle by PayPal invoice, wire transfer, or Monero.',
  url:
    (import.meta.env.VITE_PUBLIC_URL as string | undefined)?.replace(/\/$/, '') ||
    'https://eventforge.loboforge.com',
  tagline: 'GPU inference capacity as a service — custom LoRAs, manual billing, production queue',
} as const

export type PageSeoOptions = {
  title?: string
  description?: string
  canonicalPath?: string
  ogType?: string
  jsonLd?: object | object[]
  noindex?: boolean
}

export function absoluteUrl(path: string, siteUrl = SITE.url): string {
  if (!path) return siteUrl
  if (/^https?:\/\//i.test(path)) return path
  return `${siteUrl}${path.startsWith('/') ? path : `/${path}`}`
}

function upsertMeta(attr: 'name' | 'property', key: string, content: string) {
  let el = document.head.querySelector(`meta[${attr}="${key}"]`) as HTMLMetaElement | null
  if (!el) {
    el = document.createElement('meta')
    el.setAttribute(attr, key)
    document.head.appendChild(el)
  }
  el.content = content
}

function upsertLink(rel: string, href: string) {
  let el = document.head.querySelector(`link[rel="${rel}"]`) as HTMLLinkElement | null
  if (!el) {
    el = document.createElement('link')
    el.rel = rel
    document.head.appendChild(el)
  }
  el.href = href
}

function upsertJsonLd(data: object | object[] | undefined) {
  const id = 'ef-jsonld'
  const existing = document.getElementById(id)
  if (existing) existing.remove()
  if (!data) return
  const script = document.createElement('script')
  script.id = id
  script.type = 'application/ld+json'
  script.textContent = JSON.stringify(data)
  document.head.appendChild(script)
}

export function applyPageSeo(opts: PageSeoOptions = {}) {
  const title = opts.title ?? SITE.title
  const description = opts.description ?? SITE.description
  const canonical = absoluteUrl(opts.canonicalPath ?? '/')
  const ogType = opts.ogType ?? 'website'

  document.title = title
  upsertMeta('name', 'description', description)
  upsertMeta('name', 'robots', opts.noindex ? 'noindex, nofollow' : 'index, follow, max-snippet:-1')
  upsertLink('canonical', canonical)
  upsertMeta('property', 'og:site_name', SITE.name)
  upsertMeta('property', 'og:title', title)
  upsertMeta('property', 'og:description', description)
  upsertMeta('property', 'og:type', ogType)
  upsertMeta('property', 'og:url', canonical)
  upsertMeta('name', 'twitter:card', 'summary')
  upsertMeta('name', 'twitter:title', title)
  upsertMeta('name', 'twitter:description', description)
  upsertJsonLd(opts.jsonLd)
}

export type JsonLdPlan = {
  name: string
  price_usd: number
  credits: number
  description: string
}

export type JsonLdFaq = {
  question: string
  answer: string
}

export function buildLandingJsonLd(plans: JsonLdPlan[] = [], faq: JsonLdFaq[] = []) {
  const blocks: object[] = [
    {
      '@context': 'https://schema.org',
      '@type': 'Organization',
      name: SITE.name,
      url: SITE.url,
      description: SITE.description,
      email: 'sales@loboforge.com',
      parentOrganization: {
        '@type': 'Organization',
        name: 'LoboForge',
        url: 'https://www.loboforge.com/',
      },
    },
    {
      '@context': 'https://schema.org',
      '@type': 'Product',
      name: 'EventForge GPU Inference Credits',
      description:
        'Prepaid credits for GPU inference: image, video, music, and text generation with custom LoRA support, priority queue tiers, and results over WebSocket.',
      brand: { '@type': 'Brand', name: SITE.name },
      url: `${SITE.url}/#pricing`,
      offers: plans.map((p) => ({
        '@type': 'Offer',
        name: `${p.name} — ${p.credits.toLocaleString('en-US')} credits`,
        price: String(p.price_usd),
        priceCurrency: 'USD',
        description: p.description,
        url: `${SITE.url}/request`,
        availability: 'https://schema.org/InStock',
      })),
    },
    {
      '@context': 'https://schema.org',
      '@type': 'WebSite',
      name: SITE.name,
      url: SITE.url,
      description: SITE.description,
    },
  ]
  if (faq.length > 0) {
    blocks.push({
      '@context': 'https://schema.org',
      '@type': 'FAQPage',
      mainEntity: faq.map((f) => ({
        '@type': 'Question',
        name: f.question,
        acceptedAnswer: { '@type': 'Answer', text: f.answer },
      })),
    })
  }
  return blocks
}
