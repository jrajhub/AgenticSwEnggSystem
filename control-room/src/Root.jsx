import { useState } from 'react'
import App from './App'
import Shortener from './Shortener'

export default function Root() {
  const [tab, setTab] = useState('short')
  return (
    <>
      <div className="seg" role="tablist" style={{ marginBottom: 18, width: 'fit-content' }}>
        <button role="tab" aria-pressed={tab === 'short'} onClick={() => setTab('short')}>URL shortener</button>
        <button role="tab" aria-pressed={tab === 'demo'} onClick={() => setTab('demo')}>Workflow demo</button>
      </div>
      {tab === 'short' ? <Shortener /> : <App />}
    </>
  )
}
