import { ManagedAttachmentsPanel } from "./managed-attachments-panel"
import { useEffect, useState } from "react"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription } from "@/components/ui/dialog"

type Source = { id:string; path:string; fileName:string; sourceStatus:string; sourceSize:number; sourceSha256AtLink?:string }
type Hit = { name:string; path:string; extension:string }
type Legacy = { id:string; fileName:string }
async function request<T>(url:string, data?:unknown):Promise<T> {
 const r=await fetch(url,data===undefined?undefined:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)})
 if(!r.ok)throw new Error(await r.text());return r.json()
}
function ExternalAttachmentsPanel({itemId,locked=false,lockedHint,onChanged,publicPath}:{publicPath:string;itemId:string;locked?:boolean;lockedHint?:string;onChanged?:()=>void|Promise<void>}) {
 const [sources,setSources]=useState<Source[]>([]),[legacy,setLegacy]=useState<Legacy[]>([]),[open,setOpen]=useState(false),[query,setQuery]=useState(''),[path,setPath]=useState(''),[hits,setHits]=useState<Hit[]>([]),[error,setError]=useState(''),[busy,setBusy]=useState(false),[count,setCount]=useState(0)
 async function refresh(){const [s,l]=await Promise.all([request<Source[]>('/api/items/'+itemId+'/nas-references'),request<Legacy[]>('/api/items/'+itemId+'/attachments')]);setSources(s);setLegacy(l)}
 useEffect(()=>{setError('');setHits([]);setSources([]);setLegacy([]);let active=true;Promise.all([request<Source[]>('/api/items/'+itemId+'/nas-references'),request<Legacy[]>('/api/items/'+itemId+'/attachments'),request<{fileName:string}>('/api/items/'+itemId)]).then(([s,l,item])=>{if(active){setSources(s);setLegacy(l);setQuery(item.fileName)}}).catch(e=>{if(active)setError(String(e))});return()=>{active=false}},[itemId])
 async function search(){setBusy(true);setError('');try{const d=await request<{hits:Hit[];estimatedTotalHits:number}>(publicPath+'api/search',{q:query});setHits(d.hits);setCount(d.estimatedTotalHits)}catch(e){setError(String(e))}finally{setBusy(false)}}
 async function link(p:string){setBusy(true);setError('');try{await request('/api/items/'+itemId+'/nas-references',{path:p});await refresh();await onChanged?.();setOpen(false);setPath('')}catch(e){setError(String(e))}finally{setBusy(false)}}
 return <div className="space-y-3">
  <p className="text-sm text-muted-foreground">NAS source links — files remain at their original indexed locations. No upload or local source copy.</p>
  {locked&&<p>{lockedHint||'This item is locked; source links cannot be changed.'}</p>}
  {sources.map(s=><div key={s.id} className="rounded-lg border p-3 space-y-2">
   <div className="font-medium">{s.fileName}</div><div className="break-all whitespace-pre-wrap text-xs text-muted-foreground">{s.path}</div>
   <p className={s.sourceStatus==='available'?'text-sm':'text-sm text-amber-600'}>{s.sourceStatus==='available'?'Original available':s.sourceStatus==='changed'?'Source changed since linking — check its revision':s.sourceStatus==='missing'?'Original missing or moved — relink the correct source':'Source or index unavailable'}</p>
   <div className="flex gap-3 text-sm"><a className="text-primary underline" href={'/api/nas-references/'+s.id+'/file'}>Open original</a><a className="text-primary underline" href={publicPath+'?source='+encodeURIComponent(s.path)} target="_blank" rel="noreferrer">Preview / locate</a></div>
  </div>)}
  {!sources.length&&<p className="text-sm">No NAS source linked yet.</p>}
  <Button variant="outline" disabled={locked} onClick={()=>{setOpen(true);setError('')}}>+ Link from NAS index</Button>
  {error&&!open&&<p role="alert" className="text-sm text-destructive">{error}</p>}
  {legacy.length>0&&<details className="text-sm"><summary>Retained uploads ({legacy.length}) — not NAS references</summary><p className="text-muted-foreground">Retained for rollback. Files without a verified NAS match have not been substituted or deleted.</p>{legacy.map(l=><div key={l.id}><a className="underline" href={'/api/attachments/'+l.id+'/file'}>{l.fileName}</a></div>)}</details>}
  <Dialog open={open} onOpenChange={setOpen}><DialogContent className="max-w-3xl max-h-[85vh] overflow-y-auto"><DialogHeader><DialogTitle>Link an indexed NAS original</DialogTitle><DialogDescription>The record stores its source path. The original stays on the NAS; no file is copied.</DialogDescription></DialogHeader>
   <div className="flex gap-2"><Input aria-label="NAS filename or product code" value={query} onChange={e=>setQuery(e.target.value)} onKeyDown={e=>{if(e.key==='Enter')void search()}}/><Button disabled={busy} onClick={()=>void search()}>Search NAS</Button></div>
   <p className="text-xs text-muted-foreground">{count?`${count} estimated matches; showing up to 50. `:''}Index coverage is partial. Missing results do not prove absence. <a href={publicPath} target="_blank" rel="noreferrer" className="underline">View coverage and folder filters</a></p>
   {hits.map(h=><div key={h.path} className="border rounded p-2"><div className="font-medium">{h.name}</div><p className="text-xs whitespace-pre-wrap break-all">{h.path}</p><Button size="sm" variant="outline" disabled={busy} onClick={()=>void link(h.path)}>Link this original</Button></div>)}
   <label className="text-sm">Or paste an exact indexed source path (CAD, ZIP or document)</label><Input aria-label="Exact NAS source path" value={path} onChange={e=>setPath(e.target.value)} placeholder="models/assembly.STEP"/><Button disabled={busy||!path} onClick={()=>void link(path)}>Link exact path</Button>
   {error&&<p role="alert" className="text-sm text-destructive">{error}</p>}
  </DialogContent></Dialog>
 </div>
}


function AttachmentsPanel(props:Parameters<typeof ManagedAttachmentsPanel>[0]) {
 const [config,setConfig]=useState<{enabled:boolean;publicPath:string}|null>(null)
 useEffect(()=>{fetch('/api/file-reference-capabilities').then(r=>{if(!r.ok)throw Error();return r.json()}).then(c=>setConfig(c)).catch(()=>setConfig({enabled:false,publicPath:"/cad/"}))},[])
 if(config===null)return null
 return config.enabled?<ExternalAttachmentsPanel {...props} publicPath={config.publicPath}/>:<ManagedAttachmentsPanel {...props}/>
}
export { AttachmentsPanel }
