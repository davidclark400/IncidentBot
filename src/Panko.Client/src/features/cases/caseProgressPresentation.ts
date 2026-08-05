export function formatProgressDuration(milliseconds: number) {
  const safeMilliseconds = Number.isFinite(milliseconds) ? Math.max(0, milliseconds) : 0
  if (safeMilliseconds < 1000) return `${Math.round(safeMilliseconds)} ms`

  const seconds = safeMilliseconds / 1000
  if (seconds < 10) return `${seconds.toFixed(1).replace(/\.0$/, '')} s`
  if (seconds < 60) return `${Math.round(seconds)} s`

  const wholeMinutes = Math.floor(seconds / 60)
  const remainingSeconds = Math.round(seconds % 60)
  return remainingSeconds === 0 ? `${wholeMinutes} min` : `${wholeMinutes} min ${remainingSeconds} s`
}

export function sourceDisplayName(source: string) {
  const normalized = source.toLowerCase().replace(/[^a-z0-9]/g, '')
  return sourceNames[normalized] ?? source
}

const sourceNames: Record<string, string> = {
  consul: 'Consul',
  gitlab: 'GitLab',
  grafana: 'Grafana',
  kafka: 'Kafka',
  nomad: 'Nomad',
  pagerduty: 'PagerDuty',
  victorialogs: 'VictoriaLogs',
}
