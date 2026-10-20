import { setLanguage } from '../src/i18n'

try {
  localStorage.clear()
} catch {
  // Ignore storage failures in the test environment.
}
setLanguage('zh')
