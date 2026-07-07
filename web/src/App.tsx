import { LandingPage } from './LandingPage'
import OpsApp from './OpsApp'
import DashboardApp from './DashboardApp'
import { Route } from './router'

export default function App() {
  return (
    <>
      <Route path="/">
        <LandingPage />
      </Route>
      <Route path="/ops">
        <OpsApp />
      </Route>
      <Route path="/dashboard">
        <DashboardApp />
      </Route>
    </>
  )
}
