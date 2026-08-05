import { useEffect, useState } from 'react'
import { getDemoAvailability, resetDemo, type DemoAvailability } from './demoApi'

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
      window.location.assign(demo.caseUrl)
    } catch {
      setStarting(false)
    }
  }

  return { demo, starting, startDemo }
}
