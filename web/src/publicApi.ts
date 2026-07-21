// Client for the public catalog, account, and capacity-request endpoints.
// The backend contract is being built in parallel — every read endpoint has a
// static fallback so the page renders correctly if an endpoint 404s in dev.

export type PublicPlan = {
  id: string
  name: string
  description: string
  price_usd: number
  credits: number
  features: string[]
}

export type PlansResponse = {
  plans: PublicPlan[]
  enterprise_contact: string
  custom?: { enterprise_contact?: string }
}

export type PublicModel = {
  id: string
  name: string
  kind: string
  description: string
  supports_custom_loras: boolean
}

export type ModelsResponse = {
  models: PublicModel[]
}

export type RegisterResponse = {
  account_id: string
  email: string
  created_at: string
}

export type LoginResponse = {
  session_token: string
  account_id: string
  email: string
}

export type AccountResponse = {
  account_id: string
  email: string
  company?: string | null
  credits: number
  api_key: string | null
  created_at: string
}

export type CapacityRequestInput = {
  email: string
  company?: string
  name?: string
  models: string[]
  estimated_jobs: number
  notes?: string
  preferred_payment: 'paypal' | 'wire' | 'monero' | 'any'
}

export type CapacityRequestResponse = {
  request_id: string
  status: 'received'
  message: string
}

export const ENTERPRISE_CONTACT = 'sales@loboforge.com'

export const FALLBACK_PLANS: PublicPlan[] = [
  {
    id: 'starter',
    name: 'Starter',
    description: 'For prototypes and side projects. Full API access, every model, custom LoRAs included.',
    price_usd: 29,
    credits: 1000,
    features: [
      '1,000 credits',
      'All models, all modalities',
      'Custom LoRA uploads',
      'WebSocket + polling results',
      'Artifact storage included',
    ],
  },
  {
    id: 'pro',
    name: 'Pro',
    description: 'For apps in production. More credits per dollar and priority queue placement.',
    price_usd: 99,
    credits: 4000,
    features: [
      '4,000 credits',
      'All models, all modalities',
      'Custom LoRA uploads',
      'Priority queue tiers',
      'WebSocket + polling results',
      'Artifact storage included',
    ],
  },
  {
    id: 'scale',
    name: 'Scale',
    description: 'For high-volume workloads. Best credit rate, built for sustained production traffic.',
    price_usd: 299,
    credits: 14000,
    features: [
      '14,000 credits',
      'All models, all modalities',
      'Custom LoRA uploads',
      'Priority queue tiers',
      'Bulk tier for batch workloads',
      'Artifact storage included',
    ],
  },
]

export const FALLBACK_MODELS: PublicModel[] = [
  {
    id: 'wan-2-2-t2v',
    name: 'Wan 2.2 14B Text-to-Video',
    kind: 'video',
    description: 'High-fidelity text-to-video generation from the Wan 2.2 14B family.',
    supports_custom_loras: true,
  },
  {
    id: 'wan-2-2-i2v',
    name: 'Wan 2.2 14B Image-to-Video',
    kind: 'video',
    description: 'Animate still images into motion with Wan 2.2 14B image-to-video.',
    supports_custom_loras: true,
  },
  {
    id: 'flux-2-klein',
    name: 'FLUX.2 Klein',
    kind: 'image',
    description: 'Fast, high-quality image generation with the FLUX.2 Klein model.',
    supports_custom_loras: true,
  },
  {
    id: 'flux-2-klein-edit',
    name: 'FLUX.2 Klein Edit',
    kind: 'image-edit',
    description: 'Instruction-based image editing built on FLUX.2 Klein.',
    supports_custom_loras: true,
  },
  {
    id: 'z-image-turbo',
    name: 'Z-Image Turbo',
    kind: 'image',
    description: 'Ultra-fast image generation for latency-sensitive workloads.',
    supports_custom_loras: true,
  },
  {
    id: 'chroma-hd',
    name: 'Chroma HD',
    kind: 'image',
    description: 'High-definition image generation with strong aesthetic quality.',
    supports_custom_loras: true,
  },
  {
    id: 'ltx-2-video',
    name: 'LTX-2 Video',
    kind: 'video',
    description: 'Efficient long-form video generation with the LTX-2 architecture.',
    supports_custom_loras: true,
  },
  {
    id: 'ace-step',
    name: 'ACE-Step',
    kind: 'music',
    description: 'Text-to-music generation: full tracks with vocals and instrumentation.',
    supports_custom_loras: false,
  },
  {
    id: 'joycaption',
    name: 'JoyCaption',
    kind: 'captioning',
    description: 'Detailed, uncensored image captioning for datasets and pipelines.',
    supports_custom_loras: false,
  },
  {
    id: 'dolphin-llm',
    name: 'Dolphin LLM',
    kind: 'text',
    description: 'Text generation and chat with streaming token output over WebSocket.',
    supports_custom_loras: false,
  },
]

const SESSION_TOKEN_STORAGE = 'eventforge_session_token'

export function getSessionToken(): string {
  try {
    return localStorage.getItem(SESSION_TOKEN_STORAGE) ?? ''
  } catch {
    return ''
  }
}

export function setSessionToken(token: string) {
  try {
    if (token) localStorage.setItem(SESSION_TOKEN_STORAGE, token)
    else localStorage.removeItem(SESSION_TOKEN_STORAGE)
  } catch {
    /* storage unavailable (private mode) — session just won't persist */
  }
}

export class PublicApiError extends Error {
  status: number
  code: string

  constructor(status: number, code: string, message: string) {
    super(message)
    this.status = status
    this.code = code
  }
}

async function publicFetch<T>(path: string, init: RequestInit = {}, auth = false): Promise<T> {
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json')
  if (init.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  if (auth) {
    const token = getSessionToken()
    if (!token) throw new PublicApiError(401, 'no_session', 'Not signed in')
    headers.set('Authorization', `Bearer ${token}`)
  }
  let resp: Response
  try {
    resp = await fetch(path, { ...init, headers })
  } catch {
    throw new PublicApiError(0, 'network_error', 'Network error — check your connection and try again.')
  }
  if (!resp.ok) {
    let code = `http_${resp.status}`
    let message = `${resp.status} ${resp.statusText}`
    try {
      const body = await resp.json()
      if (body && typeof body.error === 'string') {
        code = body.error
        message = typeof body.message === 'string' ? body.message : body.error
      }
    } catch {
      /* non-JSON error body */
    }
    throw new PublicApiError(resp.status, code, message)
  }
  return resp.json() as Promise<T>
}

export async function fetchPlans(): Promise<PlansResponse> {
  try {
    // Backend returns { plans, custom: { enterprise_contact } }; normalize for UI.
    const data = await publicFetch<PlansResponse & { custom?: { enterprise_contact?: string } }>(
      '/v1/public/plans',
    )
    if (Array.isArray(data.plans) && data.plans.length > 0) {
      const contact =
        data.custom?.enterprise_contact || data.enterprise_contact || ENTERPRISE_CONTACT
      return { plans: data.plans, enterprise_contact: contact, custom: data.custom }
    }
  } catch {
    /* endpoint not live yet — use fallback */
  }
  return { plans: FALLBACK_PLANS, enterprise_contact: ENTERPRISE_CONTACT }
}

export async function fetchModels(): Promise<ModelsResponse> {
  try {
    const data = await publicFetch<ModelsResponse>('/v1/public/models')
    if (Array.isArray(data.models) && data.models.length > 0) return data
  } catch {
    /* endpoint not live yet — use fallback */
  }
  return { models: FALLBACK_MODELS }
}

export function register(email: string, password: string, company?: string): Promise<RegisterResponse> {
  const body: Record<string, string> = { email, password }
  if (company) body.company = company
  return publicFetch<RegisterResponse>('/v1/public/register', { method: 'POST', body: JSON.stringify(body) })
}

export async function login(email: string, password: string): Promise<LoginResponse> {
  const data = await publicFetch<LoginResponse>('/v1/public/login', {
    method: 'POST',
    body: JSON.stringify({ email, password }),
  })
  setSessionToken(data.session_token)
  return data
}

export function fetchAccount(): Promise<AccountResponse> {
  return publicFetch<AccountResponse>('/v1/public/account', {}, true)
}

export function submitCapacityRequest(input: CapacityRequestInput): Promise<CapacityRequestResponse> {
  const token = getSessionToken()
  return publicFetch<CapacityRequestResponse>(
    '/v1/public/capacity-request',
    { method: 'POST', body: JSON.stringify(input) },
    Boolean(token),
  )
}

export function formatCredits(n: number): string {
  return n.toLocaleString('en-US')
}
