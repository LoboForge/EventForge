export const SITE = {
  name: 'EventForge',
  title: 'EventForge — Production GPU Fleet as a Service',
  description:
    'Production-ready GPU fleet as a service. No provisioning your own boxes, no fleet management, no manual scaling. Submit jobs, subscribe to events, pay per job. HTTP queue + WebSocket event bus for image, video, and LLM workloads.',
  url:
    (import.meta.env.VITE_PUBLIC_URL as string | undefined)?.replace(/\/$/, '') ||
    'https://eventforge.loboforge.com',
  tagline: 'GPU fleet as a service — pay per job',
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

export function buildLandingJsonLd() {
  return [
    {
      '@context': 'https://schema.org',
      '@type': 'Organization',
      name: SITE.name,
      url: SITE.url,
      description: SITE.description,
    },
    {
      '@context': 'https://schema.org',
      '@type': 'SoftwareApplication',
      name: SITE.name,
      applicationCategory: 'DeveloperApplication',
      operatingSystem: 'Web',
      offers: {
        '@type': 'Offer',
        price: '0',
        priceCurrency: 'USD',
        description: 'Usage-based pricing per GPU job; contact for integration.',
      },
      description: SITE.description,
      url: SITE.url,
    },
    {
      '@context': 'https://schema.org',
      '@type': 'WebSite',
      name: SITE.name,
      url: SITE.url,
      description: SITE.description,
    },
  ]
}
