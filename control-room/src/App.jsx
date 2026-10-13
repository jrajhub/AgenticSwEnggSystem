import { useState, useRef, useMemo } from 'react'
import { SC, CHAOS, COL, TONE, sleep, chain, verify, layout } from './data'

export default function App() {
 const [scn,setScn]=useState('greenfield'),[chaos,setChaos]=useState('none'),[auto,setAuto]=useState(false),[speed,setSpeed]=useState(1.5);
 const [st,setSt]=useState({}),[evs,setEvs]=useState([]),[m,setM]=useState(null),[arts,setArts]=useState({}),[res,setRes]=useState(null);
 const [tab,setTab]=useState('log'),[ask,setAsk]=useState(null),[busy,setBusy]=useState(false),[forged,setForged]=useState(false);
 const R=useRef({run:0});const sc=SC[scn];const L=useMemo(()=>layout(sc.nodes),[scn]);
 const shown=forged?evs.map((e,i)=>i===1?{...e,msg:e.msg+' (forged)'}:e):evs;const ok=verify(shown);

 function reset(){R.current.run++;setSt({});setEvs([]);setM(null);setArts({});setRes(null);setBusy(false);setAsk(null);setForged(false)}
 async function start(){
  reset();const id=R.current.run;const cancelled=()=>R.current.run!==id;const nodes=sc.nodes;
  const S={},ev=[],a={},M={attempts:0,succ:0,retries:0,rb:0,replans:0,gate:0,viol:0,appr:0,rec:[]};
  const t0=Date.now(),failAt={};let stopped=null,anyFail=false,injected=false,pendingChange=false;
  const done=[],need=new Set(),run={};nodes.forEach(n=>S[n.id]='pending');setBusy(true);
  const sl=ms=>sleep(ms/speed);
  const push=()=>{setSt({...S});setEvs([...ev]);setM({...M,rec:[...M.rec]});setArts({...a})};
  const log=(type,node,msg,tone='info')=>{const seq=ev.length+1,prev=ev.length?ev[ev.length-1].hash:'0',e={seq,type,node:node||'-',msg,tone,t:(Date.now()-t0)/1000,prev};e.hash=chain(e,0,prev);ev.push(e);push()};
  const set=(n,s)=>{S[n]=s;log('Status',n,s,s==='succeeded'?'ok':s==='failed'?'err':s==='rolledback'?'rb':s==='awaiting'?'warn':'info')};
  const approve=async(n,title)=>{M.appr++;set(n.id,'awaiting');log('Approval',n.id,'requested: '+title,'warn');
   let d;if(auto){await sl(500);d=(chaos==='reject'&&n.id==='release')?'reject':'approve'}
   else d=await new Promise(r=>setAsk({n,title,r}));setAsk(null);if(cancelled())return 'reject';
   log('Approval',n.id,d+' by '+(auto?'scripted approver':'you'),d==='approve'?'ok':'err');set(n.id,'running');return d};
  const stop=r=>{if(!stopped){stopped=r;log('SafeStop','-',r,'err')}};
  const terminal=(n,r)=>{anyFail=true;if(n.comp)need.add(n.id);set(n.id,'failed');log('Failed',n.id,r,'err');stop(`node '${n.id}' failed: ${r}`)};
  const inject=(n,at)=>{
   if(chaos==='transient'&&((n.id==='implement'&&at<=2)||(n.id==='run-tests'&&at<=1)))return{msg:'simulated transient failure (timeout)'};
   if(chaos==='policy'&&n.id==='implement'&&at===1)return{sev:'high',msg:'[policy] insecure-code/weak-hash found in source-bundle'};
   if(chaos==='secret'&&n.id==='implement')return{sev:'critical',msg:'[policy] CRITICAL secret-scan/aws-access-key in source-bundle'};
   if(chaos==='rollback'&&n.id==='release')return{msg:'publish step failed after partial write'};return null};
  const runNode=async n=>{
   if(n.approval==='exec'){log('Gate',n.id,'entry gate release-readiness: pass','ok');const d=await approve(n,'Approve execution of '+n.id);
    if(d!=='approve')return terminal(n,'approval rejected: action never executed')}
   let at=0;const max=(n.id==='release'?1:2)+1;
   while(true){
    if(stopped){set(n.id,'skipped');if(n.comp)need.add(n.id);return}
    at++;M.attempts++;set(n.id,'running');log('Attempt',n.id,`agent run, attempt ${at}`);
    await sl(900+(n.id.length*70));if(cancelled())return;
    if(stopped){set(n.id,'skipped');if(n.comp)need.add(n.id);return}
    let f=inject(n,at);
    if(!f){log('Gate',n.id,'exit gates + policy guard: pass','ok');
     if(n.approval==='commit'){const d=await approve(n,'Approve outputs of '+n.id);
      if(d==='reject')f={perm:1,msg:'rejected by human'};else if(d==='changes')f={gate:1,msg:'changes requested by human: rework with feedback'}}}
    if(!f){const from=n.deps.flatMap(d=>nodes.find(x=>x.id===d).out);
     n.out.forEach(o=>{a[o]={v:(a[o]?.v||0)+1,by:n.id,from}});M.succ++;
     if(failAt[n.id]){M.rec.push((Date.now()-failAt[n.id])/1000);delete failAt[n.id]}
     done.push(n.id);log('Commit',n.id,n.out.map(o=>o+'@v'+a[o].v).join(', '),'ok');set(n.id,'succeeded');
     if(scn==='ambiguous'&&n.id==='design'&&!injected){injected=true;pendingChange=true}return}
    if(f.sev||f.gate)M.gate++;if(f.sev)M.viol++;failAt[n.id]=failAt[n.id]||Date.now();
    log('Failed',n.id,f.msg,f.sev==='critical'?'err':'warn');
    if(f.sev==='critical'||f.perm)return terminal(n,f.msg);
    if(at<max){M.retries++;log('Retry',n.id,f.sev?'feedback sent to agent; retrying':`retry ${at}/${max-1} with backoff`,'warn');await sl(450*at);continue}
    return terminal(n,'retries exhausted: '+f.msg)}};
  log('Run','-',`workflow '${scn}' started, chaos=${chaos}`);
  while(!cancelled()){
   if(pendingChange){pendingChange=false;log('Change','-','requirements@v2 submitted by human:product-owner (late change)','warn');
    ['decompose','design'].forEach(x=>{if(S[x]==='succeeded'){M.replans++;S[x]='pending';const i=done.indexOf(x);if(i>=0)done.splice(i,1);log('Replan',x,'input requirements v1 -> v2; node re-planned','warn')}});push()}
   if(!stopped)nodes.filter(n=>S[n.id]==='pending'&&n.deps.every(d=>S[d]==='succeeded')&&!run[n.id]).forEach(n=>{S[n.id]='running';run[n.id]=runNode(n).finally(()=>{delete run[n.id]})});
   const ps=Object.values(run);if(!ps.length)break;await Promise.race(ps)}
  if(cancelled())return;
  if(stopped){const t=[...need,...[...done].reverse().filter(x=>nodes.find(n=>n.id===x).comp&&S[x]==='succeeded')];
   for(const x of new Set(t)){await sl(450);const was=S[x];M.rb++;log('Rollback',x,'side effects compensated','rb');if(was==='succeeded')set(x,'rolledback')}}
  nodes.forEach(n=>{if(S[n.id]==='pending')S[n.id]='blocked'});
  const status=!stopped&&nodes.every(n=>S[n.id]==='succeeded')?'Completed':anyFail?'Failed':'SafeStopped';
  log('Run','-',status+(stopped?': '+stopped:''),status==='Completed'?'ok':'err');
  setRes({status,reason:stopped,secs:(Date.now()-t0)/1000});setBusy(false)}

 const mt=m&&[['Attempt success',m.attempts?Math.round(100*m.succ/m.attempts)+'%':'-'],['Retries',m.retries],['Rollbacks',m.rb],['Re-plans',m.replans],['Gate failures',m.gate],['Policy violations',m.viol],['Approvals',m.appr],['MTTR',m.rec.length?(m.rec.reduce((x,y)=>x+y,0)/m.rec.length).toFixed(1)+'s':'n/a']];
 const bc=res?(res.status==='Completed'?'--ok':'--bad'):null;

 return (
  <div>
   <h1>Agentic SDLC control room</h1>
   <p className="sub">Watch a governed workflow turn a requirement into a reviewed release. Agents work; the engine enforces gates, approvals, retries, rollback and an audit trail. This page is a browser simulation of the .NET engine&apos;s behavior; the real run is the <span className="mono">agentic</span> CLI.</p>
   <div className="panel bar">
    <div className="f">Scenario
     <div className="seg" role="group">
      {Object.entries(SC).map(([k, v]) => (
       <button key={k} aria-pressed={scn === k} disabled={busy} onClick={() => { setScn(k); reset() }}>{v.label}</button>
      ))}
     </div>
    </div>
    <label className="f">Inject a failure
     <select value={chaos} disabled={busy} onChange={e => setChaos(e.target.value)}>
      {CHAOS.map(([k, v]) => <option key={k} value={k}>{v}</option>)}
     </select>
    </label>
    <label className="f">Speed
     <input type="range" min="0.5" max="4" step="0.5" value={speed} onChange={e => setSpeed(+e.target.value)} />
    </label>
    <label className="chk"><input type="checkbox" checked={auto} disabled={busy} onChange={e => setAuto(e.target.checked)} />Auto-approve checkpoints</label>
    <button className="btn" disabled={busy} onClick={start}>{res ? 'Run again' : 'Run workflow'}</button>
    <button className="btn g" onClick={reset}>Reset</button>
   </div>

   <div className="grid">
    <div className="panel">
     <div style={{ color: 'var(--mute)', marginBottom: 8 }}>{sc.blurb}</div>
     <div className="board">
      <svg width={L.w} height={L.h} role="img" aria-label="Workflow dependency graph">
       {sc.nodes.flatMap(n => n.deps.map(d => {
        const a = L.pos[d], b = L.pos[n.id]
        return <path key={d + n.id} d={`M${a.x + 142},${a.y + 22} C${a.x + 158},${a.y + 22} ${b.x - 16},${b.y + 22} ${b.x},${b.y + 22}`} fill="none" stroke="var(--line)" strokeWidth="2" />
       }))}
       {sc.nodes.map(n => {
        const p = L.pos[n.id], s = st[n.id] || 'pending', c = `var(${COL[s]})`
        return (
         <g key={n.id} className={'node ' + (s === 'running' || s === 'awaiting' ? 'pulse' : '')} transform={`translate(${p.x},${p.y})`}>
          <rect width="142" height="44" rx="8" fill="var(--panel)" stroke={c} strokeWidth={s === 'pending' ? 1.5 : 2.5} strokeDasharray={s === 'blocked' || s === 'skipped' ? '4 3' : undefined} />
          <rect width="6" height="44" rx="3" fill={c} />
          <text x="16" y="19" fontSize="13" fontWeight="700" fill="var(--ink)">{n.id}</text>
          <text x="16" y="35" fontSize="11" fill="var(--mute)">{s}{n.approval ? ' · human gate' : ''}{n.comp && s !== 'pending' ? ' · undoable' : ''}</text>
         </g>
        )
       })}
      </svg>
     </div>
     <div className="lg">
      {[['pending', '--idle'], ['running', '--run'], ['awaiting approval', '--warn'], ['succeeded', '--ok'], ['failed', '--bad'], ['rolled back', '--rb']].map(([l, c]) => (
       <span key={l}><i className="dot" style={{ background: `var(${c})` }} />{l}</span>
      ))}
     </div>
     {res && (
      <div className="banner" style={{ borderColor: `var(${bc})`, color: `var(${bc})` }}>
       {res.status} in {res.secs.toFixed(1)}s{res.reason ? `: ${res.reason}` : '. All gates passed and the release was approved.'}
      </div>
     )}
    </div>

    <div className="panel">
     <div className="tabs" role="tablist">
      {[['log', 'Audit log'], ['met', 'Reliability'], ['art', 'Artifacts']].map(([k, l]) => (
       <button key={k} role="tab" aria-selected={tab === k} onClick={() => setTab(k)}>{l}</button>
      ))}
     </div>

     {tab === 'log' && (
      <div>
       <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8, alignItems: 'center', marginBottom: 8, fontSize: 13 }}>
        <span style={{ color: `var(${evs.length ? (ok ? '--ok' : '--bad') : '--mute'})`, fontWeight: 700 }}>
         {evs.length ? (ok ? 'Hash chain verified' : 'Chain broken: tampering detected') : 'Audit log is empty. Run a workflow.'}
        </span>
        {evs.length > 1 && <button className="btn g" style={{ padding: '4px 10px' }} onClick={() => setForged(!forged)}>{forged ? 'Undo tamper' : 'Try to tamper'}</button>}
       </div>
       <div className="log">
        {shown.slice().reverse().map(e => (
         <div className="ev" key={e.seq}>
          <span className="t">{e.t.toFixed(1)}s</span>
          <span className="tag" style={{ color: `var(${TONE[e.tone] || '--mute'})` }}>{e.type}</span>
          <span>{e.node !== '-' && <b>{e.node} </b>}{e.msg}<div className="mono">#{e.hash.slice(0, 10)}</div></span>
         </div>
        ))}
       </div>
      </div>
     )}

     {tab === 'met' && (mt ? (
      <div className="mt">{mt.map(([l, v]) => <div className="m" key={l}><b>{v}</b><span>{l}</span></div>)}</div>
     ) : <p style={{ color: 'var(--mute)' }}>Metrics appear while the workflow runs.</p>)}

     {tab === 'art' && (Object.keys(arts).length ? (
      <div className="log">
       {Object.entries(arts).map(([k, v]) => (
        <div className="art" key={k}>
         <b>{k}</b> <span className="mono">v{v.v}</span>
         <div style={{ color: 'var(--mute)' }}>produced by {v.by}{v.from.length ? '; derived from ' + v.from.join(', ') : ''}</div>
        </div>
       ))}
      </div>
     ) : <p style={{ color: 'var(--mute)' }}>Versioned artifacts and their lineage show up here.</p>)}
    </div>
   </div>

   <p className="foot">Try it: run Greenfield with &quot;Leaked secret&quot; to see a critical policy violation stop the run and roll back the sandbox. Run Ambiguous to watch a late requirement change re-plan design. Turn off auto-approve to act as the human approver.</p>

   {ask && (
    <div className="modal" role="dialog" aria-modal="true">
     <div className="panel">
      <h2 style={{ margin: '0 0 6px', fontSize: 20 }}>{ask.title}</h2>
      <p style={{ color: 'var(--mute)', margin: '0 0 14px' }}>{ask.n.approval === 'exec' ? 'High-impact action. Nothing runs until you approve.' : 'High-impact output. It is committed to shared state only after you approve.'}</p>
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
       <button className="btn ok" onClick={() => ask.r('approve')}>Approve</button>
       {ask.n.approval === 'commit' && <button className="btn g" onClick={() => ask.r('changes')}>Request changes</button>}
       <button className="btn bad" onClick={() => ask.r('reject')}>Reject</button>
      </div>
     </div>
    </div>
   )}
  </div>
 )
}
