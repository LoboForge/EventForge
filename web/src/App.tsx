import { LandingPage } from './LandingPage'
import { SignupPage, LoginPage } from './SignupPage'
import OpsApp from './OpsApp'
import DashboardApp from './DashboardApp'
import { Route } from './router'

export default function App() {
  return (
    <>
      <Route path="/">
        <LandingPage />
      </Route>
      <Route path="/signup">
        <SignupPage />
      </Route>
      <Route path="/request">
        <SignupPage />
      </Route>
      <Route path="/login">
        <LoginPage />
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
