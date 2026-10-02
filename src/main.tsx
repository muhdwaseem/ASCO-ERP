import { StrictMode, useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import './index.css'
import './App.css'
import './print.css'
import './ess.css'
import './modules.css'
import './themes.css'
import { EssPortal } from './components/EssPortal.tsx'
import App from './App.tsx'
import { SignIn } from './components/SignIn.tsx'
import { sessionActions, useSession } from './api/client.ts'

const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: 30_000, retry: 1, refetchOnWindowFocus: false } },
})

function Root() {
  const s = useSession()
  const [checking, setChecking] = useState(s.mode !== 'demo')
  // A still-valid cookie from an earlier visit signs straight back in.
  useEffect(() => {
    if (s.mode === 'demo') return
    sessionActions.restore().finally(() => setChecking(false))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  if (checking) return <div className="splash"><div className="start-logo">A</div><span>ASCO</span></div>
  if (s.mode === 'live' && s.me?.employee && s.me.companies.length === 0) return <EssPortal />
  if (s.mode === 'demo' || (s.mode === 'live' && s.companyId)) return <App key={`${s.mode}-${s.companyId}`} />
  return <SignIn />
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <Root />
    </QueryClientProvider>
  </StrictMode>,
)
