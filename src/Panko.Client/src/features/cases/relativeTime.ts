export function relativeTime(value: string) {
  const differenceSeconds = Math.round((new Date(value).getTime() - Date.now()) / 1000)
  const absoluteSeconds = Math.abs(differenceSeconds)
  const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })
  if (absoluteSeconds < 60) return formatter.format(differenceSeconds, 'second')
  if (absoluteSeconds < 3600) return formatter.format(Math.round(differenceSeconds / 60), 'minute')
  if (absoluteSeconds < 86400) return formatter.format(Math.round(differenceSeconds / 3600), 'hour')
  return formatter.format(Math.round(differenceSeconds / 86400), 'day')
}
