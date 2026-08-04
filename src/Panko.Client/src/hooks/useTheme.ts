import { useEffect, useState } from 'react'

export type ThemeControl = {
  darkMode: boolean
  toggleTheme: () => void
}

export function useTheme(): ThemeControl {
  const [darkMode, setDarkMode] = useState(() => {
    const savedTheme = window.localStorage.getItem('panko-theme')
    return savedTheme ? savedTheme === 'dark' : window.matchMedia('(prefers-color-scheme: dark)').matches
  })

  useEffect(() => {
    document.documentElement.classList.toggle('dark', darkMode)
    window.localStorage.setItem('panko-theme', darkMode ? 'dark' : 'light')
  }, [darkMode])

  return {
    darkMode,
    toggleTheme: () => setDarkMode((current) => !current),
  }
}
