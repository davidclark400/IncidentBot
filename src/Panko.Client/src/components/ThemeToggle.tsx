import type { ThemeControl } from '../hooks/useTheme'

export function ThemeToggle({ darkMode, toggleTheme }: ThemeControl) {
  return (
    <button
      type="button"
      onClick={toggleTheme}
      aria-label={darkMode ? 'Switch to light mode' : 'Switch to dark mode'}
      title={darkMode ? 'Switch to light mode' : 'Switch to dark mode'}
      className="inline-flex size-10 items-center justify-center rounded-md border border-border bg-background text-foreground shadow-sm hover:bg-accent hover:text-accent-foreground sm:size-9"
    >
      {darkMode ? (
        <svg aria-hidden="true" viewBox="0 0 24 24" className="size-4" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><circle cx="12" cy="12" r="4" /><path d="M12 2v2M12 20v2M4.93 4.93l1.42 1.42M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.42-1.42M17.66 6.34l1.41-1.41" /></svg>
      ) : (
        <svg aria-hidden="true" viewBox="0 0 24 24" className="size-4" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79Z" /></svg>
      )}
    </button>
  )
}
