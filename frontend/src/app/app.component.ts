import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { Employee, Issue, PayrollImport, PayrollItem } from './models';

@Component({
 selector:'app-root', standalone:true, imports:[CommonModule,FormsModule],
 templateUrl:'./app.component.html', styleUrl:'./app.component.css'
})
export class AppComponent {
 private http=inject(HttpClient);
 readonly api='http://localhost:5055/api';
 files=signal<File[]>([]);
 result=signal<PayrollImport|null>(null);
 selected=signal<Employee|null>(null);
 search=signal('');
 busy=signal(false);
 error=signal('');
 tab=signal<'employees'|'payments'|'covers'|'issues'>('employees');
 view=signal<'all'|'errors'>('all');
 importList=signal<{id:string;importedAt:string;employees:number;payments:number;covers:number;blocking:boolean}[]>([]);
 employees=computed(()=> {
   const data=this.result(); if(!data)return [];
   let filtered=data.employees;
   const q=this.search().trim();
   if(q) filtered=filtered.filter(e=>[e.name,e.nationalId,e.paymentNumber||''].some(s=>s.includes(q)));
   if(this.view()==='errors') filtered=filtered.filter(e=>e.issues.some(i=>i.severity===2));
   return filtered;
 });
 overallIssues=computed<Issue[]>(()=> {
   const data=this.result();
   return data ? [...data.issues, ...data.employees.flatMap(e=>e.issues.map(i=>({...i,message:e.name+' — '+i.message})))] : [];
 });
 readonly money = (v:number|null|undefined)=>v==null?'—':new Intl.NumberFormat('ar-EG',{minimumFractionDigits:2,maximumFractionDigits:2}).format(v);
 readonly typeName = (v:number)=> ['غير معروف','صرفية موظف','كشوف صرف','كافر'][v]??'غير معروف';
 readonly itemKind = (v:number)=>['استحقاق','استقطاع','حصة جهة العمل','إجمالي تجميعي'][v]??'غير معروف';
 constructor(){this.loadPrevious();}
 onFiles(event:Event){
   const input=event.target as HTMLInputElement;
   const fs=Array.from(input.files||[]).filter(f=>f.name.toLowerCase().endsWith('.pdf')).slice(0,3);
   this.files.set(fs);this.error.set('');
 }
 removeFile(index:number){this.files.update(files=>files.filter((_,i)=>i!==index));}
 import(){
   if(!this.files().length||this.busy())return;
   const form=new FormData();
   for(const f of this.files()) form.append('files',f,f.name);
   this.busy.set(true);this.error.set('');
   this.http.post<PayrollImport>(this.api+'/imports',form).subscribe({
    next:report=>{this.result.set(report);this.selected.set(null);this.busy.set(false);this.loadPrevious();this.tab.set(report.employees.length?'employees':report.payments.length?'payments':report.covers.length?'covers':'issues');},
    error:e=>{this.busy.set(false);this.error.set(e.error?.error||'تعذر الاتصال بالـAPI. تأكد من تشغيل الخادم على localhost:5055');}
   });
 }
 loadPrevious(){
   this.http.get<typeof this.importList extends never ? never : {id:string;importedAt:string;employees:number;payments:number;covers:number;blocking:boolean}[]>(this.api+'/imports')
       .subscribe({next:rows=>this.importList.set(rows),error:()=>{}});
 }
 openPrevious(id:string){
  this.http.get<PayrollImport>(this.api+'/imports/'+id).subscribe({next:b=>{this.result.set(b);this.selected.set(null)},error:()=>this.error.set('تعذر فتح الاستيراد السابق')});
 }
 showEmployee(e:Employee){this.selected.set(e);}
 closeEmployee(){this.selected.set(null);}
 exportEmployees(){
  const b=this.result();if(!b)return;
  const escape=(value:string|number|null|undefined)=> {
    const text=String(value??'');
    // Avoid CSV formula injection when a user opens exports in Excel.
    const safe=/^[=+@\-\t\r]/.test(text)?"'"+text:text;
    return '"'+safe.replace(/"/g,'""')+'"';
  };
  const rows=[['الاسم','الرقم القومي','رقم الصرف','البنك','إجمالي الاستحقاقات','إجمالي الاستقطاعات','حصة جهة العمل','الصافي','عدد التنبيهات'],
   ...b.employees.map(e=>[e.name,e.nationalId,e.paymentNumber??'',e.bank??'',e.earnings??'',e.deductions??'',e.employerContributions??'',e.net??'',e.issues.length])];
  const blob=new Blob(['\uFEFF'+rows.map(r=>r.map(escape).join(',')).join('\r\n')],{type:'text/csv;charset=utf-8'});
  const url=URL.createObjectURL(blob); const a=document.createElement('a');a.href=url;a.download='oracle-payroll-employees.csv';a.click();URL.revokeObjectURL(url);
 }
 trackItem(_i:number,item:PayrollItem){return item.accountCode+'-'+item.originalName+'-'+item.sourcePage;}
}
