export const N=(id,deps,o={})=>({id,deps,out:[id==='analyze'?'requirements':id],...o});
export const SC={
 greenfield:{label:'Greenfield',blurb:'Well-specified requirement: full pipeline with parallel branches and a release approval.',nodes:[
  N('analyze',[],{out:['requirements']}),N('decompose',['analyze'],{out:['task-plan']}),
  N('design',['decompose'],{out:['design-doc'],approval:'commit'}),N('risk',['design'],{out:['risk-register']}),
  N('implement',['design'],{out:['source-bundle'],comp:1}),N('author-tests',['design'],{out:['test-bundle'],comp:1}),
  N('run-tests',['implement','author-tests'],{out:['test-report']}),N('review',['implement'],{out:['security-review']}),
  N('docs',['risk'],{out:['user-docs']}),N('release',['run-tests','review','docs'],{out:['release-notes'],approval:'exec',comp:1}),
  N('summarize',['release'],{out:['engineering-summary']})]},
 brownfield:{label:'Brownfield',blurb:'Add link expiry to existing code: impact analysis, exact-match patches, regression gate.',nodes:[
  N('analyze',[],{out:['requirements']}),N('impact',['analyze'],{out:['impact-analysis']}),N('decompose',['impact'],{out:['task-plan']}),
  N('design',['decompose'],{out:['design-doc'],approval:'commit'}),N('risk',['design'],{out:['risk-register']}),
  N('implement',['design'],{out:['source-bundle'],comp:1}),N('author-tests',['design'],{out:['test-bundle'],comp:1}),
  N('run-tests',['implement','author-tests'],{out:['test-report']}),N('review',['implement'],{out:['security-review']}),
  N('docs',['risk'],{out:['user-docs']}),N('release',['run-tests','review','docs'],{out:['release-notes'],approval:'exec',comp:1}),
  N('summarize',['release'],{out:['engineering-summary']})]},
 ambiguous:{label:'Ambiguous',blurb:'Vague brief: human clarification, then a late stakeholder change re-plans design.',nodes:[
  N('analyze',[],{out:['requirements','open-questions']}),N('clarify',['analyze'],{out:['clarifications']}),N('decompose',['analyze'],{out:['task-plan']}),
  N('design',['clarify','decompose'],{out:['design-doc'],approval:'commit'}),N('risk',['design'],{out:['risk-register']}),
  N('test-plan',['design'],{out:['test-plan']}),N('docs',['risk'],{out:['user-docs']}),
  N('readiness',['risk','test-plan'],{out:['readiness-report'],approval:'commit'}),N('summarize',['readiness','docs'],{out:['engineering-summary']})]}};
export const CHAOS=[['none','None: happy path'],['transient','Transient failures: retries + MTTR'],['policy','Insecure code: policy block, self-corrects'],['secret','Leaked secret: critical, safe-stop, rollback'],['rollback','Release fails: compensation'],['reject','Reviewer rejects release']];
export const COL={pending:'--idle',running:'--run',awaiting:'--warn',succeeded:'--ok',failed:'--bad',rolledback:'--rb',blocked:'--idle',skipped:'--idle'};
export const TONE={ok:'--ok',warn:'--warn',err:'--bad',info:'--mute',rb:'--rb'};
export const sleep=ms=>new Promise(r=>setTimeout(r,ms));
export function h53(s){let a=0xdeadbeef,b=0x41c6ce57;for(let i=0;i<s.length;i++){const c=s.charCodeAt(i);a=Math.imul(a^c,2654435761);b=Math.imul(b^c,1597334677)}a=Math.imul(a^a>>>16,2246822507)^Math.imul(b^b>>>13,3266489909);b=Math.imul(b^b>>>16,2246822507)^Math.imul(a^a>>>13,3266489909);return(4294967296*(2097151&b)+(a>>>0)).toString(16).padStart(14,'0')}
export const chain=(e,i,p)=>h53(p+'|'+e.seq+'|'+e.type+'|'+e.node+'|'+e.msg);
export function verify(evs){let p='0';for(const e of evs){if(e.prev!==p||e.hash!==chain(e,0,e.prev))return false;p=e.hash}return true}

export function layout(nodes){const lv={};nodes.forEach(n=>lv[n.id]=n.deps.length?Math.max(...n.deps.map(d=>lv[d]))+1:0);
 const cols={};nodes.forEach(n=>(cols[lv[n.id]]=cols[lv[n.id]]||[]).push(n.id));const pos={};
 const maxRows=Math.max(...Object.values(cols).map(c=>c.length));
 Object.entries(cols).forEach(([c,ids])=>ids.forEach((id,i)=>pos[id]={x:12+c*168,y:14+(i+(maxRows-ids.length)/2)*66}));
 return {pos,w:12+(Object.keys(cols).length)*168,h:28+maxRows*66}}

