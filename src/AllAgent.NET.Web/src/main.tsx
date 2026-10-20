import { createRoot } from 'react-dom/client'
import { MotionConfig } from 'motion/react'
import './i18n'
import App from './App'
import './styles/app.css'
import 'highlight.js/styles/github.css'
createRoot(document.getElementById('app')!).render(<MotionConfig reducedMotion="user"><App /></MotionConfig>)
