import { LandingPage } from './LandingPage'
import OpsApp from './OpsApp'
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
    </>
  )
}
