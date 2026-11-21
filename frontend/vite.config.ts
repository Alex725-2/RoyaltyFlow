import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv } from 'vite'

export default defineConfig(({ mode }) => {
  const { ROYALTYFLOW_API_TARGET } = loadEnv(mode, '.', 'ROYALTYFLOW_')
  const upstream = new URL(ROYALTYFLOW_API_TARGET || 'https://royalty.investment-tracker.ru')
  const loopback = ['localhost', '127.0.0.1', '[::1]'].includes(upstream.hostname)
  if (
    (upstream.protocol !== 'https:' && !(loopback && upstream.protocol === 'http:')) ||
    upstream.username || upstream.password || upstream.pathname !== '/' || upstream.search || upstream.hash
  ) {
    throw new Error('ROYALTYFLOW_API_TARGET must be an HTTPS origin or an HTTP loopback origin.')
  }
  const proxy = Object.fromEntries(
    ['/api', '/swagger', '/health'].map(path => [path, { target: upstream.origin, changeOrigin: true }]),
  )
  return {
    plugins: [react()],
    server: { host: '127.0.0.1', proxy },
    preview: { host: '127.0.0.1', proxy },
  }
})
