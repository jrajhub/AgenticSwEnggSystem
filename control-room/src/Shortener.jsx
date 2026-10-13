import { useEffect, useState } from 'react'

const HISTORY_KEY = 'shortener-history'

export default function Shortener() {
  const [base, setBase] = useState(() => localStorage.getItem('api-base') || 'http://localhost:5080')
  const [url, setUrl] = useState('')
  const [alias, setAlias] = useState('')
  const [items, setItems] = useState(() => {
    try { return JSON.parse(localStorage.getItem(HISTORY_KEY) || '[]') } catch { return [] }
  })
  const [err, setErr] = useState(null)
  const [busy, setBusy] = useState(false)
  const [online, setOnline] = useState(null)
  const [copied, setCopied] = useState('')

  const api = path => base.replace(/\/$/, '') + path

  useEffect(() => {
    localStorage.setItem('api-base', base)
    let live = true
    setOnline(null)
    fetch(api('/healthz')).then(r => live && setOnline(r.ok)).catch(() => live && setOnline(false))
    return () => { live = false }
  }, [base])

  useEffect(() => { localStorage.setItem(HISTORY_KEY, JSON.stringify(items)) }, [items])

  async function shorten(e) {
    e.preventDefault()
    setErr(null)
    setBusy(true)
    try {
      const body = { url: url.trim() }
      if (alias.trim()) body.alias = alias.trim()
      const r = await fetch(api('/api/v1/links'), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      })
      const data = await r.json().catch(() => ({}))
      if (!r.ok) {
        throw new Error(r.status === 429 ? 'Rate limit reached: 30 links per minute.' : data.detail || data.title || `Request failed (${r.status})`)
      }
      const item = { code: data.code, shortUrl: data.shortUrl, target: data.targetUrl, clicks: 0, existing: r.status === 200 }
      setItems(prev => [item, ...prev.filter(i => i.code !== item.code)].slice(0, 20))
      setUrl('')
      setAlias('')
    } catch (ex) {
      setErr(ex instanceof TypeError ? `Cannot reach the API at ${base}. Is it running (and is CORS enabled)?` : ex.message)
    } finally {
      setBusy(false)
    }
  }

  async function refresh(code) {
    try {
      const r = await fetch(api(`/api/v1/links/${code}/stats`))
      if (!r.ok) throw new Error()
      const s = await r.json()
      setItems(prev => prev.map(i => (i.code === code ? { ...i, clicks: s.clicks, last: s.lastAccessedAt } : i)))
    } catch {
      setErr('Could not load stats. The link may be gone (the service stores links in memory only).')
    }
  }

  async function copy(text) {
    try { await navigator.clipboard.writeText(text); setCopied(text); setTimeout(() => setCopied(''), 1500) } catch { /* ignore */ }
  }

  const dot = online === null ? '--idle' : online ? '--ok' : '--bad'
  const latest = items[0]

  return (
    <div>
      <h1>URL shortener</h1>
      <p className="sub">Paste a long address and get a short one. This talks to the real .NET service you run locally.</p>

      <div className="panel" style={{ marginBottom: 14 }}>
        <label className="f">API address
          <input className="inp" value={base} onChange={e => setBase(e.target.value)} spellCheck={false} />
        </label>
        <div style={{ marginTop: 8, fontSize: 13 }}>
          <i className="dot" style={{ background: `var(${dot})` }} />
          {online === null ? 'Checking...' : online ? 'API online' : 'API not reachable'}
          {online === false && <div className="mono" style={{ marginTop: 6 }}>dotnet run --project src/UrlShortener.Api --urls http://localhost:5080</div>}
        </div>
      </div>

      <form className="panel" onSubmit={shorten}>
        <label className="f">Long URL
          <input className="inp" type="url" required placeholder="https://example.com/some/very/long/page" value={url} onChange={e => setUrl(e.target.value)} />
        </label>
        <label className="f" style={{ marginTop: 12 }}>Custom alias (optional, 4 to 32 letters, digits, - or _)
          <input className="inp" placeholder="my-link" value={alias} onChange={e => setAlias(e.target.value)} />
        </label>
        <div style={{ marginTop: 14 }}>
          <button className="btn" type="submit" disabled={busy || !url.trim()}>{busy ? 'Shortening...' : 'Shorten'}</button>
        </div>
        {err && <div className="banner" style={{ borderColor: 'var(--bad)', color: 'var(--bad)' }}>{err}</div>}
        {latest && !err && (
          <div className="banner" style={{ borderColor: 'var(--ok)' }}>
            {latest.existing ? 'You already shortened this URL: ' : 'Created: '}
            <a href={latest.shortUrl} target="_blank" rel="noreferrer">{latest.shortUrl}</a>{' '}
            <button type="button" className="btn g" style={{ padding: '3px 10px' }} onClick={() => copy(latest.shortUrl)}>
              {copied === latest.shortUrl ? 'Copied' : 'Copy'}
            </button>
          </div>
        )}
      </form>

      {items.length > 0 && (
        <div className="panel" style={{ marginTop: 14 }}>
          <b>Your links</b>
          <div className="log" style={{ marginTop: 8 }}>
            {items.map(i => (
              <div className="art" key={i.code}>
                <a href={i.shortUrl} target="_blank" rel="noreferrer"><b>{i.code}</b></a>
                <span className="mono"> {i.clicks} click{i.clicks === 1 ? '' : 's'}</span>
                <div style={{ color: 'var(--mute)', wordBreak: 'break-all' }}>{i.target}</div>
                <button type="button" className="btn g" style={{ padding: '2px 10px', marginRight: 6, marginTop: 4 }} onClick={() => refresh(i.code)}>Refresh clicks</button>
                <button type="button" className="btn g" style={{ padding: '2px 10px', marginTop: 4 }} onClick={() => copy(i.shortUrl)}>{copied === i.shortUrl ? 'Copied' : 'Copy'}</button>
              </div>
            ))}
          </div>
          <p className="foot">Links live in the service&apos;s memory. Restart it and they are gone (the list here is only your browser&apos;s memory of them).</p>
        </div>
      )}
    </div>
  )
}
