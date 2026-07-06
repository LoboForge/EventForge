import { createContext, useContext, useEffect, useState, type AnchorHTMLAttributes, type ReactNode } from 'react'

type RouterContextValue = {
  path: string
  navigate: (to: string) => void
}

const RouterContext = createContext<RouterContextValue | null>(null)

function readPath() {
  const raw = window.location.pathname.replace(/\/+$/, '') || '/'
  return raw
}

export function RouterProvider({ children }: { children: ReactNode }) {
  const [path, setPath] = useState(readPath)

  useEffect(() => {
    const onPop = () => setPath(readPath())
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [])

  const navigate = (to: string) => {
    const next = to.startsWith('/') ? to : `/${to}`
    if (next !== window.location.pathname) {
      window.history.pushState(null, '', next)
    }
    setPath(readPath())
  }

  return <RouterContext.Provider value={{ path, navigate }}>{children}</RouterContext.Provider>
}

export function useRouter() {
  const ctx = useContext(RouterContext)
  if (!ctx) throw new Error('useRouter must be used within RouterProvider')
  return ctx
}

type LinkProps = AnchorHTMLAttributes<HTMLAnchorElement> & { to: string }

export function Link({ to, onClick, href, ...rest }: LinkProps) {
  const { navigate } = useRouter()
  return (
    <a
      {...rest}
      href={to}
      onClick={(e) => {
        if (e.metaKey || e.ctrlKey || e.shiftKey || e.altKey || rest.target === '_blank') return
        e.preventDefault()
        navigate(to)
        onClick?.(e)
      }}
    />
  )
}

export function Route({ path, children }: { path: string; children: ReactNode }) {
  const { path: current } = useRouter()
  const normalized = path.replace(/\/+$/, '') || '/'
  const match =
    normalized === '/'
      ? current === '/'
      : current === normalized || current.startsWith(`${normalized}/`)
  return match ? <>{children}</> : null
}
