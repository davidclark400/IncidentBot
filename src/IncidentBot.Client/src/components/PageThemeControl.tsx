import { ThemeToggle } from './ThemeToggle'
import type { ThemeControl } from '../hooks/useTheme'

export function PageThemeControl({ darkMode, toggleTheme }: ThemeControl) {
  return (
    <div className="absolute right-4 top-4 sm:right-5 sm:top-5">
      <ThemeToggle darkMode={darkMode} toggleTheme={toggleTheme} />
    </div>
  )
}
