type PankoBrandProps = {
  className?: string
  href?: string
  markClassName?: string
  subtitle?: string
}

export function PankoBrand({ className = '', href, markClassName = 'size-8', subtitle }: PankoBrandProps) {
  const content = (
    <>
      <img
        src="/brand/panko-mark.svg"
        alt=""
        className={`${markClassName} shrink-0 rounded-[22%]`}
        width="32"
        height="32"
      />
      <div>
        <p className="text-sm font-semibold leading-none tracking-[0.01em] text-foreground">Panko</p>
        {subtitle && <p className="mt-1 hidden text-[11px] text-muted-foreground sm:block">{subtitle}</p>}
      </div>
    </>
  )

  return href
    ? <a href={href} className={`flex items-center gap-3 rounded-md ${className}`} aria-label="Panko operations home">{content}</a>
    : <div className={`flex items-center gap-3 ${className}`}>{content}</div>
}
