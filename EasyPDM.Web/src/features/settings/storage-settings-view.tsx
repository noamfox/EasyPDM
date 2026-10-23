import { ManagedStorageSettingsView } from "./managed-storage-settings-view"
import { useEffect,useState } from "react"
type Info={mode:string;referenceCount:number;fileCount:number;totalSizeBytes:number;source:string;originalsReadOnly:boolean}
function ExternalStorageSettingsView(){
 const [info,setInfo]=useState<Info|null>(null),[error,setError]=useState('')
 useEffect(()=>{fetch('/api/settings/storage').then(async r=>{if(!r.ok)throw Error('Storage settings unavailable');setInfo(await r.json())}).catch(e=>setError(String(e)))},[])
 return <div className="mx-auto max-w-3xl space-y-5"><h2 className="text-xl font-semibold">File storage · NAS references</h2><section className="rounded-xl border p-5 space-y-3"><h3 className="font-semibold">Originals stay on the NAS</h3><p>EasyPDM records hold links to indexed files in the configured archive. Opening a linked file reads the original from its NAS location. It is not uploaded into /data/storage.</p><p>Use <strong>Link from NAS index</strong> on an item to select its source. Moving or changing a source is reported on that item; a different file is never silently substituted.</p><p><a className="text-primary underline" href="/cad/">Find indexed CAD files and check coverage →</a></p>{info&&<p><strong>{info.referenceCount} NAS source links</strong> · original archive mounted read-only</p>}</section><section className="rounded-xl border p-5 space-y-2"><h3 className="font-semibold">Previews</h3><p>3D previews are disposable derived caches. They do not replace source CAD or create managed copies of the originals.</p></section><section className="rounded-xl border p-5 space-y-2"><h3 className="font-semibold">Retained uploads</h3>{info&&<p>{info.fileCount} previous files · {(info.totalSizeBytes/1024/1024).toFixed(1)} MB retained for rollback.</p>}<p>Verified matches open their NAS originals. Unmatched uploads remain labelled separately. New uploads and disk-location changes are disabled in reference mode. No existing files have been deleted.</p></section><section className="rounded-xl border p-5 space-y-2"><h3 className="font-semibold">Backup scope</h3><p>The EasyPDM database holds source links and item metadata. NAS originals need their existing NAS backup and version history; a PDM backup is not a backup of those files.</p></section>{error&&<p role="alert">{error}</p>}</div>
}


function StorageSettingsView(){
 const [enabled,setEnabled]=useState<boolean|null>(null)
 useEffect(()=>{fetch('/api/file-reference-capabilities').then(r=>{if(!r.ok)throw Error();return r.json()}).then(c=>setEnabled(c.enabled)).catch(()=>setEnabled(false))},[])
 if(enabled===null)return null
 return enabled?<ExternalStorageSettingsView/>:<ManagedStorageSettingsView/>
}
export { StorageSettingsView }
