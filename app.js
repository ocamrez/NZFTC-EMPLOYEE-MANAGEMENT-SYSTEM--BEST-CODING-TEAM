'use strict';
// Frontend demonstration only. Role selection is not authentication.
// window.name keeps fictional state across local HTML pages in the same tab.
const initialState = () => ({version:1,role:null,profile:{email:'alex.taylor@example.com',phone:'',address:''},requests:[]});
let state=initialState();
try { const saved=JSON.parse(window.name || '{}'); if(saved.nzftcDemo?.version===1) state=saved.nzftcDemo; } catch {}
const save=()=>{window.name=JSON.stringify({nzftcDemo:state});};
const $=id=>document.getElementById(id);
const escapeHtml=value=>String(value).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const balance=()=>20-state.requests.filter(r=>r.status==='Approved'&&r.type==='Annual leave').reduce((n,r)=>n+r.days,0);
const reserved=()=>state.requests.filter(r=>r.status==='Pending'&&r.type==='Annual leave').reduce((n,r)=>n+r.days,0);
function weekdays(start,end){let count=0;const date=new Date(start+'T12:00:00Z'),last=new Date(end+'T12:00:00Z');for(;date<=last;date.setUTCDate(date.getUTCDate()+1)){if(date.getUTCDay()!==0&&date.getUTCDay()!==6)count++;}return count;}
const page=document.body.dataset.page;
const employeePages=['dashboard.html','leave.html','payroll.html','profile.html'];
let allowed=true;
if(employeePages.includes(page)&&state.role!=='employee')allowed=false;
if(page==='admin.html'&&state.role!=='admin')allowed=false;
if(!allowed)$('page-content').innerHTML='<section class="card"><h1>Choose the right demo account</h1><p>This screen belongs to a different account view.</p><a class="button" href="login.html">Choose demo account</a></section>';
let links=[['MANAGE_NZFTC.html','Home']];
if(state.role==='employee')links.push(['dashboard.html','Dashboard'],['leave.html','Leave'],['payroll.html','Payroll'],['profile.html','Profile']);
if(state.role==='admin')links.push(['admin.html','Administration']);
links.push(['about.html','About / Contact'],['login.html',state.role?'Switch demo account':'Login']);
$('navigation').innerHTML=links.map(([url,title])=>`<a href="${url}" ${page===url?'aria-current="page"':''}>${title}</a>`).join('')+(state.role?'<button class="secondary" id="logout">Sign out</button>':'');
$('logout')?.addEventListener('click',()=>{state.role=null;save();location.href='MANAGE_NZFTC.html';});
$('reset').addEventListener('click',()=>{if(confirm('Reset all fictional demo changes?')){state=initialState();save();location.href='MANAGE_NZFTC.html';}});
$('demo-login')?.addEventListener('submit',event=>{event.preventDefault();state.role=$('role').value;save();location.href=state.role==='admin'?'admin.html':'dashboard.html';});
if($('balance'))$('balance').textContent=balance();
if($('pending'))$('pending').textContent=state.requests.filter(r=>r.status==='Pending').length;
if($('greeting'))$('greeting').textContent='Welcome back, Alex';
function message(id,text,error=false){$(id).textContent=text;$(id).className=error?'error':'success';}
function renderRequests(admin=false){const target=$(admin?'admin-requests':'requests');if(!target)return;
if(!state.requests.length){target.innerHTML='<p class="muted">No leave requests yet. Submit one using the employee demo.</p>';return;}
target.innerHTML='<table><thead><tr><th>Employee / type</th><th>Dates</th><th>Days</th><th>Note</th><th>Status</th><th>Action</th></tr></thead><tbody>'+state.requests.map(r=>`<tr><td>Alex Taylor<br>${escapeHtml(r.type)}</td><td>${escapeHtml(r.start)} to ${escapeHtml(r.end)}</td><td>${r.days}</td><td>${escapeHtml(r.reason||'—')}</td><td><span class="badge ${escapeHtml(r.status)}">${escapeHtml(r.status)}</span></td><td>${r.status==='Pending'?(admin?`<button data-id="${r.id}" data-action="Approved">Approve</button> <button class="secondary" data-id="${r.id}" data-action="Declined">Decline</button>`:`<button class="secondary" data-id="${r.id}" data-action="Cancelled">Cancel</button>`):'—'}</td></tr>`).join('')+'</tbody></table>';
target.querySelectorAll('[data-action]').forEach(button=>button.addEventListener('click',()=>{const r=state.requests.find(x=>x.id===button.dataset.id);if(!r||r.status!=='Pending')return;
if(admin&&state.role!=='admin')return;
if(button.dataset.action==='Approved'&&r.type==='Annual leave'&&r.days>balance()){message('admin-message','Not enough annual leave available.',true);return;}
r.status=button.dataset.action;save();renderRequests(admin);if($('balance'))$('balance').textContent=balance();if(admin)message('admin-message',`Demo request ${r.status.toLowerCase()}.`);
}));}
renderRequests(page==='admin.html');
$('leave-form')?.addEventListener('submit',event=>{event.preventDefault();const start=$('start').value,end=$('end').value,type=$('leave-type').value;
if(!start||!end||end<start){message('leave-message','Choose an end date on or after the start date.',true);return;}
const days=weekdays(start,end);
if(days<1||days>366){message('leave-message','Choose a date range containing 1 to 366 weekdays.',true);return;}
if(state.requests.some(r=>!['Declined','Cancelled'].includes(r.status)&&start<=r.end&&end>=r.start)){message('leave-message','These dates overlap an existing pending or approved request.',true);return;}
if(type==='Annual leave'&&days>balance()-reserved()){message('leave-message','This request exceeds your available annual leave after pending requests.',true);return;}
state.requests.push({id:Date.now().toString(36),type,start,end,days,reason:$('reason').value.trim(),status:'Pending'});save();renderRequests();event.target.reset();message('leave-message',`Demo request submitted for ${days} weekday(s). Status: Pending.`);
});
if($('profile-form')){for(const key of ['email','phone','address'])$(key).value=state.profile[key];$('profile-form').addEventListener('submit',event=>{event.preventDefault();for(const key of ['email','phone','address'])state.profile[key]=$(key).value.trim();save();message('profile-message','Demo contact details saved for this tab.');});}
document.querySelectorAll('[data-slip]').forEach(button=>button.addEventListener('click',()=>{$('slip-period').textContent=button.dataset.slip;$('payslip').showModal();}));
$('close-slip').addEventListener('click',()=>$('payslip').close());
$('print-slip').addEventListener('click',()=>window.print());
