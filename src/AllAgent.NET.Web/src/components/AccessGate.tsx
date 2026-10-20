import { useEffect, useState, type ReactNode } from 'react'
import { KeyRound, QrCode, RefreshCw, ShieldCheck } from 'lucide-react'
import { getAccessToken, setAccessToken } from '@/bridge'
import { useT } from '@/i18n'
import { Button } from './ui/b-button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from './ui/card'
import { Input } from './ui/input'

interface AuthStatus {
  required: boolean
  authenticated: boolean
  lan: boolean
  passwordConfigured: boolean
  urls: string[]
  localUrl: string
}

/**
 * Password/token gate for a LAN listener. Requests from the host itself are always allowed by
 * the backend, so this screen appears only on other devices. A QR link carries a per-run token;
 * a manually set password is the alternative when there is no QR at hand.
 */
export function AccessGate({ children }: { children: ReactNode }) {
  const tr = useT()
  const [status, setStatus] = useState<AuthStatus | null | undefined>(undefined)
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [attempt, setAttempt] = useState(0)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    let cancelled = false
    const controller = new AbortController()
    const timer = window.setTimeout(() => controller.abort(), 8000)
    const token = getAccessToken()
    const query = token ? '?access=' + encodeURIComponent(token) : ''
    setFailed(false)
    fetch('/__auth/status' + query, { cache: 'no-store', signal: controller.signal })
      .then(async response => {
        const type = response.headers.get('content-type') ?? ''
        // Vite preview and non-HTTP hosts have no auth endpoint; let the page boot as before.
        return type.includes('application/json') ? await response.json() as AuthStatus : null
      })
      .then(value => { if (!cancelled) setStatus(value) })
      .catch(() => { if (!cancelled) { setStatus(null); setFailed(true) } })
      .finally(() => window.clearTimeout(timer))
    return () => { cancelled = true; controller.abort(); window.clearTimeout(timer) }
  }, [attempt])

  if (status === undefined) {
    return <div className="access-gate"><div className="access-loading">{tr('正在检查访问权限…', 'Checking access…')}</div></div>
  }
  if (!status) {
    // A non-JSON response means a development/mock host; a failed request on a real
    // LAN page must not leave the page spinning forever.
    if (!failed) return <>{children}</>
    return <div className="access-gate">
      <Card className="access-card">
        <CardHeader>
          <div className="access-icon"><RefreshCw size={20} /></div>
          <CardTitle>{tr('无法连接后端', 'Cannot reach the backend')}</CardTitle>
          <CardDescription>{tr('页面已经加载，但没有收到宿主机的响应。请确认手机与电脑网络互通、宿主机上的 Web 工作台仍在运行，然后重试。', 'The page loaded but the host did not answer. Check that this device can reach the computer and that the Web workspace is still running, then retry.')}</CardDescription>
        </CardHeader>
        <CardContent className="access-content">
          <p className="access-hint">{tr('当前地址：', 'Current address: ')}<code>{window.location.host}</code></p>
          <p className="access-hint">{tr('如果刚刚更新过宿主机，请完全关闭旧标签页并重新扫描二维码。', 'If the host was just updated, close this tab completely and scan the QR code again.')}</p>
          <div className="access-actions">
            <Button icon={<RefreshCw size={14} />} onClick={() => setAttempt(value => value + 1)} size="sm">{tr('重试', 'Retry')}</Button>
            <Button onClick={() => window.location.reload()} size="sm" variant="outline">{tr('刷新页面', 'Reload page')}</Button>
          </div>
        </CardContent>
      </Card>
    </div>
  }
  if (!status.required || status.authenticated) return <>{children}</>

  async function login() {
    setBusy(true)
    setError('')
    try {
      const response = await fetch('/__auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ password }),
      })
      const body = await response.json().catch(() => ({})) as { token?: string; error?: string }
      if (!response.ok) throw new Error(body.error ?? tr('登录失败，请重试。', 'Login failed; try again.'))
      if (!body.token) throw new Error(tr('服务器没有返回访问令牌。', 'The server did not return an access token.'))
      setAccessToken(body.token)
      window.location.reload()
    } catch (value) {
      setError(value instanceof Error ? value.message : String(value))
    } finally {
      setBusy(false)
    }
  }

  return <div className="access-gate">
    <Card className="access-card">
      <CardHeader>
        <div className="access-icon">{status.passwordConfigured ? <KeyRound size={20} /> : <QrCode size={20} />}</div>
        <CardTitle>{tr('需要授权访问', 'Access required')}</CardTitle>
        <CardDescription>
          {status.passwordConfigured
            ? tr('这台设备正在通过局域网访问 AllAgent.NET。请输入访问密码，或扫描宿主机上的二维码直接进入。', 'This device is reaching AllAgent.NET over the LAN. Enter the access password, or scan the QR code on the host to enter directly.')
            : tr('这台设备正在通过局域网访问 AllAgent.NET。你可以输入宿主机上设置的访问密码，或扫描宿主机上的二维码直接进入。', 'This device is reaching AllAgent.NET over the LAN. Enter the access password set on the host, or scan the QR code on the host to enter directly.')}
        </CardDescription>
      </CardHeader>
      <CardContent className="access-content">
        <form className="access-form" onSubmit={event => { event.preventDefault(); void login() }}>
          <Input
            autoFocus
            label={tr('访问密码', 'Access password')}
            onValueChange={value => { setPassword(value); setError('') }}
            placeholder={status.passwordConfigured
              ? tr('输入宿主机上设置的密码', 'Enter the password set on the host')
              : tr('宿主机尚未设置密码，请先在宿主机设置', 'No password is set on the host yet; set one there first')}
            showPasswordToggle
            type="password"
            value={password}
            wrapperClassName="w-full"
          />
          {error ? <p className="access-error">{error}</p> : null}
          <Button disabled={busy || password.length === 0} loading={busy} type="submit" className="w-full">
            <ShieldCheck size={15} />{tr('使用密码进入', 'Sign in with password')}
          </Button>
        </form>
        <div className="access-qr-hint">
          <QrCode size={34} />
          <p>{tr('在运行 AllAgent.NET 的电脑上打开「设置 → 局域网访问」，用这台设备扫描二维码；也可以在终端执行 /web-ui qr 显示二维码。', 'On the AllAgent.NET host, open Settings → LAN access and scan the QR code with this device. You can also run /web-ui qr in the terminal to show it.')}</p>
          {!status.passwordConfigured ? <p>{tr('宿主机还没有设置访问密码，所以密码登录暂时不可用。请扫码进入，或让宿主机先设置密码。', 'The host has no access password yet, so password sign-in is unavailable. Scan the QR code, or set a password on the host first.')}</p> : null}
        </div>
        <Button icon={<RefreshCw size={14} />} onClick={() => setAttempt(value => value + 1)} size="sm" variant="outline">
          {tr('重新检查', 'Refresh access state')}
        </Button>
      </CardContent>
    </Card>
  </div>
}
