import { useEffect, useState } from 'react'
import type { DemoAvailability } from '../../api-client/types.gen'
import { getDemoAvailability, resetDemo } from './demoApi'

export function useDemoReplay() {
  const [demo, setDemo] = useState<DemoAvailability | null>(null)
  const [starting, setStarting] = useState(false)

  useEffect(() => {
    void getDemoAvailability().then(setDemo).catch(() => setDemo(null))
  }, [])

  const startDemo = async () => {
    if (!demo) return
    setStarting(true)
    try {
      await resetDemo()
      window.location.assign(demo.incidentUrl)
    } catch {
      setStarting(false)
    }
  }

  return { demo, starting, startDemo }
}
