import { Component, inject } from '@angular/core';import { CommonModule } from '@angular/common';import { HttpClient } from '@angular/common/http';import { FormsModule } from '@angular/forms';
interface ExportItem { key:string; title:string; kind:string; code:string|null }
interface ExportEmployee { index:number; employee:Employee }
interface Entry {accountCode:string|null;label:string;amount:number;kind:string;page:number;rawText:string}
interface Employee {sourceName?:string|null;institutionalEntityCode?:string|null;nationalId:string|null;disbursementNumber:string|null;name:string;bank:string|null;gross:number|null;deductions:number|null;net:number|null;entries:Entry[];warnings:string[];startPage:number;endPage:number}
interface Issue {code:string;message:string;page:number|null;severity:string}
interface History {id:string;batchId:string;fileName:string;period:string|null;kind:string;status:string;importedAtUtc:string;employees:number;groups:number;issues:number}
interface Group {bank:string|null;branch:string|null;employeeCount:number|null;totalNet:number|null;page:number;warnings:string[]}
interface Result {id:string;fileName:string;kind:string;status:string;period:string|null;pages:number;employees:Employee[];groups:Group[];issues:Issue[]}
@Component({selector:'app-root',standalone:true,imports:[CommonModule,FormsModule],template:`
<main class="shell"><header><div class="brand">ORACLE <span>PDF PARSER</span></div><div class="subtitle">استيراد وتحليل تقارير المرتبات • بدون إحداثيات ثابتة</div></header>
<section class="hero"><div><h1>مراجعة كشوف الرواتب بدقة</h1><p>ارفع صرفية موظف أو كافر أو كشوف صرف. لن يتم اعتماد بيانات غير مؤكدة تلقائيًا.</p></div>
<label class="upload">اختر ملفات PDF<input aria-label="اختر ملفات PDF" type="file" accept=".pdf,application/pdf" multiple (change)="select($event)"></label><button (click)="upload()" [disabled]="busy||files.length===0">{{busy?'جارٍ التحليل...':'تحليل الملفات'}}</button><p class="small">{{files.length}} ملف محدد (الحد الأقصى 3)</p></section>
<p *ngIf="error" class="error" role="alert">{{error}}</p>
<section class="panel"><div class="panelhead"><h2>سجل الاستيرادات</h2><button (click)="loadHistory()">تحديث السجل</button></div>
<div class="tablewrap" *ngIf="history.length"><table><thead><tr><th>الملف</th><th>الشهر</th><th>تاريخ الاستيراد</th><th>الموظفون</th><th>الأخطاء</th><th>الحالة</th><th>مراجعة</th></tr></thead>
<tbody><tr *ngFor="let h of history"><td>{{h.fileName}}</td><td>{{h.period||'غير محدد'}}</td><td>{{h.importedAtUtc | date:'short'}}</td><td>{{h.employees}}</td><td>{{h.issues}}</td><td>{{h.status}}</td><td><button class="plain" (click)="openHistory(h.id)">فتح</button></td></tr></tbody></table></div>
<p *ngIf="!history.length" class="small">اضغط تحديث السجل لعرض الملفات المستوردة سابقًا.</p></section>
<div *ngIf="results.length" class="actions"><button (click)="exportEmployees()">تصدير الموظفين CSV</button><button (click)="exportItems()">تصدير البنود CSV</button><button (click)="exportJson()">حفظ التقرير JSON</button></div>

<section *ngIf="exportReports.length" class="panel excel-panel" aria-label="تصدير Excel مخصص">
  <div class="panelhead"><h2>تصدير Excel مخصص</h2><span class="tag">ملف XLSX بالأرقام القابلة للحساب</span></div>
  <p class="small">اختر الموظفين والبنود المطلوبة فقط. يمكنك تصدير بند واحد، عدة بنود، الإجماليات أو الصافي في نفس الملف.</p>
  <div class="export-row">
    <label for="excelSource">مصدر البيانات</label>
    <select id="excelSource" [ngModel]="exportReportId" (ngModelChange)="onExportReportChanged($event)">
      <option *ngFor="let r of exportReports" [value]="r.id">{{r.fileName}} — {{r.kind}} — {{r.period||'شهر غير محدد'}} ({{r.employees.length}} موظف)</option>
    </select>
  </div>
  <div *ngIf="exportReport as source" class="export-sections">
    <fieldset>
      <legend>1. الموظفون</legend>
      <div class="export-row">
        <label><input type="radio" name="exportEmployeeScope" [checked]="exportAllEmployees" (change)="exportAllEmployees=true"> كل الموظفين ({{source.employees.length}})</label>
        <label><input type="radio" name="exportEmployeeScope" [checked]="!exportAllEmployees" (change)="exportAllEmployees=false"> موظف واحد أو مجموعة مختارة</label>
      </div>
      <div *ngIf="!exportAllEmployees">
        <input aria-label="البحث في موظفي التصدير" [(ngModel)]="exportEmployeeQuery" placeholder="ابحث بالاسم أو الرقم القومي أو رقم الصرف">
        <div class="export-row"><button class="plain" type="button" (click)="selectVisibleExportEmployees(true)">تحديد نتائج البحث</button><button class="plain" type="button" (click)="selectVisibleExportEmployees(false)">إلغاء تحديد النتائج</button></div>
        <div class="selection-list">
          <label *ngFor="let row of visibleExportEmployees" class="selection-option">
            <input type="checkbox" [checked]="selectedExportEmployees.has(row.index)" (change)="toggleExportEmployee(row.index,$event)">
            <span>{{row.employee.name}} <small>{{row.employee.disbursementNumber||row.employee.nationalId||''}}</small></span>
          </label>
          <p *ngIf="!visibleExportEmployees.length" class="small">لا يوجد موظفون مطابقون للبحث.</p>
        </div>
        <p class="small">عدد الموظفين المختارين: {{selectedExportEmployees.size}}</p>
      </div>
    </fieldset>
    <fieldset>
      <legend>2. البيانات المالية المطلوبة</legend>
      <div class="export-row">
        <label><input type="checkbox" [(ngModel)]="exportGross"> إجمالي الاستحقاقات</label>
        <label><input type="checkbox" [(ngModel)]="exportDeductions"> إجمالي الاستقطاعات</label>
        <label><input type="checkbox" [(ngModel)]="exportNet"> صافي المرتب</label>
      </div>
      <h3>بنود محددة (اختياري)</h3>
      <p class="small">يمكن اختيار بند أو مجموعة بنود. البند المعلوماتي هو حصة جهة العمل، وليس مبلغًا إضافيًا للموظف.</p>
      <input aria-label="البحث عن بند للتصدير" [(ngModel)]="exportItemQuery" placeholder="ابحث بكود البند أو اسمه">
      <div class="export-row" *ngIf="exportItemOptions.length">
        <button type="button" class="plain" (click)="selectVisibleExportItems(true)">تحديد البنود الظاهرة</button>
        <button type="button" class="plain" (click)="selectVisibleExportItems(false)">إلغاء تحديد البنود الظاهرة</button>
      </div>
      <div class="selection-list">
        <label *ngFor="let item of visibleExportItems" class="selection-option">
          <input type="checkbox" [checked]="selectedExportItemKeys.has(item.key)" (change)="toggleExportItem(item.key,$event)">
          <span>{{item.title}}</span>
        </label>
        <p *ngIf="!visibleExportItems.length" class="small">لا توجد بنود مطابقة أو التقرير لا يحتوي على بنود تفصيلية.</p>
      </div>
      <p class="small">عدد البنود المختارة: {{selectedExportItemKeys.size}}</p>
    </fieldset>
  </div>
  <p *ngIf="exportReport?.status !== 'Verified'" class="warning">التقرير يحتاج مراجعة؛ ملف Excel سيحتفظ بحالة التقرير والتحذيرات ويترك القيم غير المثبتة فارغة.</p>
  <div class="export-row">
    <button type="button" (click)="downloadPayrollExcel()" [disabled]="exportingExcel||(!exportAllEmployees&&!selectedExportEmployees.size)||(!exportNet&&!exportGross&&!exportDeductions&&!selectedExportItemKeys.size)">
      {{exportingExcel?'جارٍ تجهيز Excel...':'تصدير ملف Excel'}}
    </button>
    <span class="small">يتضمن الملف ملخصًا لكل موظف، وتفاصيل البنود، وصفحة ملاحظات المراجعة.</span>
  </div>
  <p class="small">الملف يحتوي بيانات شخصية ومالية. احفظه في مكان آمن.</p>
</section>
<section *ngFor="let report of results" class="panel"><div class="panelhead"><h2>{{report.fileName}}</h2><span class="tag">{{report.kind}} • {{report.period||"شهر غير محدد"}} • {{report.status}}</span></div>
<div class="stats"><div><small>الصفحات</small><b>{{report.pages}}</b></div><div><small>الموظفون المستخرجون</small><b>{{report.employees.length}}</b></div><div><small>مجموعات الصرف</small><b>{{report.groups.length}}</b></div><div><small>صافي المرتبات المستخرجة</small><b>{{sumEmployeeNet(report) | number:'1.2-2'}}</b></div><div><small>تحذيرات</small><b>{{report.issues.length}}</b></div></div>
<div *ngIf="report.issues.length" class="warning"><h3>تحتاج مراجعة</h3><p *ngFor="let issue of report.issues.slice(0,30)">{{issue.code}}: {{issue.message}} <span *ngIf="issue.page">— صفحة {{issue.page}}</span></p><p *ngIf="report.issues.length>30">و{{report.issues.length-30}} تحذير إضافي</p></div>
<div class="tablewrap" *ngIf="report.groups.length">
<h3>إجماليات السجلات والبنوك</h3>
<table><thead><tr><th>البنك</th><th>الفرع</th><th>عدد الموظفين</th><th>صافي المجموعة</th><th>صفحة المصدر</th><th>المراجعة</th></tr></thead>
<tbody><tr *ngFor="let g of report.groups"><td>{{g.bank||'—'}}</td><td>{{g.branch||'—'}}</td><td>{{g.employeeCount??'—'}}</td><td>{{g.totalNet==null?'—':(g.totalNet | number:'1.2-2')}}</td><td>{{g.page}}</td><td>{{g.warnings.join(' | ')||'متطابق'}}</td></tr></tbody></table></div>
<div class="search"><label>بحث عن موظف</label><input [(ngModel)]="search" placeholder="الاسم أو الرقم القومي أو كود الجهة أو رقم الصرف"></div>
<div class="tablewrap"><table><thead><tr><th>الاسم</th><th>الرقم القومي</th><th>كود الجهة المؤسسي</th><th>رقم الصرف</th><th>الصافي</th><th>الحالة</th><th>التفاصيل</th></tr></thead><tbody><ng-container *ngFor="let e of report.employees"><tr *ngIf="matches(e)"><td>{{e.name}}</td><td>{{e.nationalId||'—'}}</td><td>{{e.institutionalEntityCode||'—'}}</td><td>{{e.disbursementNumber||'—'}}</td><td>{{e.net ?? '—'}}</td><td>{{e.warnings.length?'مراجعة':'مكتمل'}}</td><td><button class="plain" (click)="selected=selected===e?null:e">عرض البنود</button></td></tr></ng-container></tbody></table></div>
<div class="details" *ngIf="selected as e"><h3>بنود {{e.name}}</h3><p *ngIf="e.institutionalEntityCode">كود الجهة المؤسسي: {{e.institutionalEntityCode}} (جزء من رقم الصرف المطبوع، وليس حساب بند مالي)</p><p *ngIf="e.sourceName && e.sourceName!==e.name">الاسم كما ورد في كشف الصرف: {{e.sourceName}}</p><p>الصفحات {{e.startPage}}–{{e.endPage}} • الاستحقاقات: {{e.gross??'غير مقروء'}} • الاستقطاعات: {{e.deductions??'غير مقروء'}}</p><p class="small">«معلوماتي» = حصة جهة العمل/الحكومة في التأمينات؛ تُستخدم في المطابقة ولا تُعد استحقاقًا نقديًا مستقلًا للموظف.</p><table><thead><tr><th>الحساب</th><th>البند</th><th>المبلغ</th><th>النوع</th></tr></thead><tbody><tr *ngFor="let item of e.entries"><td>{{item.accountCode}}</td><td>{{item.label}}</td><td>{{item.amount}}</td><td>{{entryKindLabel(item.kind)}}</td></tr></tbody></table></div>
</section></main>`,styles:[``]})
export class AppComponent {
 private http=inject(HttpClient);
 files:File[]=[];busy=false;error='';results:Result[]=[];history:History[]=[];search='';selected:Employee|null=null;
 exportReportId='';exportAllEmployees=true;selectedExportEmployees=new Set<number>();exportEmployeeQuery='';
 exportGross=false;exportDeductions=false;exportNet=true;exportItemQuery='';
 exportItemOptions:ExportItem[]=[];selectedExportItemKeys=new Set<string>();exportingExcel=false;
 get exportReports(){return this.results.filter(r=>!!r.id && r.employees.length>0 && (r.kind==='EmployeePayslip'||r.kind==='PaymentSheet'))}
 get exportReport(){return this.exportReports.find(r=>r.id===this.exportReportId)??null}
 get visibleExportEmployees():ExportEmployee[]{
   const q=this.exportEmployeeQuery.trim().toLowerCase();
   return this.exportReport?.employees.map((employee,index)=>({employee,index}))
     .filter(row=>!q||[row.employee.name,row.employee.nationalId,row.employee.disbursementNumber,row.employee.institutionalEntityCode].some(s=>s?.toLowerCase().includes(q)))??[];
 }
 get visibleExportItems():ExportItem[]{
   const q=this.exportItemQuery.trim().toLowerCase();
   return this.exportItemOptions.filter(e=>!q||e.title.toLowerCase().includes(q));
 }
 onExportReportChanged(id:string){
   this.exportReportId=id;this.selectedExportEmployees.clear();this.exportAllEmployees=true;
   this.selectedExportItemKeys.clear();this.exportItemQuery='';this.exportEmployeeQuery='';
   const keys=new Set<string>();this.exportItemOptions=[];
   for(const employee of this.exportReport?.employees??[])for(const entry of employee.entries??[]){
     if(!['Earning','Deduction','Informational'].includes(entry.kind))continue;
     const key=this.exportItemKey(entry);
     if(keys.has(key))continue;keys.add(key);
     const type=this.entryKindLabel(entry.kind);
     this.exportItemOptions.push({key,kind:entry.kind,code:entry.accountCode,title:type+' | '+(entry.accountCode??'بدون حساب')+' | '+entry.label});
   }
   this.exportItemOptions.sort((a,b)=>a.title.localeCompare(b.title,'ar'));
 }
 private exportItemKey(e:Entry){return e.kind+'|'+(e.accountCode??'')+'|'+e.label}
 private prepareExcelExport(){
   const preferred=this.exportReports.find(r=>r.kind==='EmployeePayslip')??this.exportReports[0];
   this.onExportReportChanged(preferred?.id??'');
 }
 toggleExportEmployee(index:number,event:Event){
   if((event.target as HTMLInputElement).checked)this.selectedExportEmployees.add(index);
   else this.selectedExportEmployees.delete(index);
 }
 selectVisibleExportEmployees(checked:boolean){
   for(const row of this.visibleExportEmployees){
     if(checked)this.selectedExportEmployees.add(row.index);
     else this.selectedExportEmployees.delete(row.index);
   }
 }
 toggleExportItem(key:string,event:Event){
   if((event.target as HTMLInputElement).checked)this.selectedExportItemKeys.add(key);
   else this.selectedExportItemKeys.delete(key);
 }
 selectVisibleExportItems(checked:boolean){
   for(const item of this.visibleExportItems){
     if(checked)this.selectedExportItemKeys.add(item.key);
     else this.selectedExportItemKeys.delete(item.key);
   }
 }
 downloadPayrollExcel(){
   const report=this.exportReport;
   if(!report){this.error='اختر تقرير موظفين أولاً';return}
   if(!this.exportAllEmployees && this.selectedExportEmployees.size===0){this.error='حدد موظفًا واحدًا على الأقل';return}
   if(!this.exportGross&&!this.exportDeductions&&!this.exportNet&&!this.selectedExportItemKeys.size){
     this.error='اختر مبلغًا أو بندًا واحدًا على الأقل';return;
   }
   this.exportingExcel=true;this.error='';
   const request={reportId:report.id,allEmployees:this.exportAllEmployees,
     employeeIndexes:this.exportAllEmployees?[]:Array.from(this.selectedExportEmployees).sort((a,b)=>a-b),
     includeGross:this.exportGross,includeDeductions:this.exportDeductions,includeNet:this.exportNet,
     itemKeys:Array.from(this.selectedExportItemKeys)};
   this.http.post('/api/exports/payroll',request,{responseType:'blob'}).subscribe({
     next:blob=>{
       const filename='oracle-payroll-'+(report.period??'export')+'.xlsx';
       this.saveBlob(filename,blob);this.exportingExcel=false;
     },
     error:()=>{this.exportingExcel=false;this.error='تعذر إنشاء ملف Excel. راجع اختيار الموظفين والبنود أو حالة الخادم.'}
   });
 }
 private saveBlob(name:string,blob:Blob){
   const url=URL.createObjectURL(blob);const a=document.createElement('a');
   a.href=url;a.download=name;document.body.appendChild(a);a.click();a.remove();URL.revokeObjectURL(url);
 }

 select(e:Event){this.files=Array.from((e.target as HTMLInputElement).files||[]).slice(0,3);this.error=''}
 upload(){if(!this.files.length)return;this.busy=true;this.error='';const form=new FormData();this.files.forEach(f=>form.append('files',f));this.http.post<Result[]>('/api/import',form).subscribe({next:r=>{this.results=r;this.selected=null;this.prepareExcelExport();this.busy=false;this.loadHistory()},error:e=>{this.error=e.error?.error||'تعذر تحليل الملف';this.busy=false}})}


 loadHistory(){this.http.get<History[]>('/api/imports?take=50').subscribe({next:r=>this.history=r,error:()=>this.error='تعذر قراءة سجل الاستيرادات'});}
 openHistory(id:string){this.http.get<Result>('/api/imports/'+encodeURIComponent(id)).subscribe({next:r=>{this.results=[r];this.selected=null;this.prepareExcelExport()},error:()=>this.error='تعذر فتح الاستيراد'});}
 private save(name:string,text:string,type:string){
   const blob=new Blob([text],{type});const url=URL.createObjectURL(blob);
   const a=document.createElement('a');a.href=url;a.download=name;document.body.appendChild(a);a.click();a.remove();
   URL.revokeObjectURL(url);
 }
 private cell(value:unknown):string{
   let s=String(value??'');
   if(/^[=+\-@\t\r]/.test(s))s="'"+s;
   return '"'+s.replace(/"/g,'""')+'"';
 }
 private csv(name:string,rows:unknown[][]){
   this.save(name,'\uFEFF'+rows.map(r=>r.map(v=>this.cell(v)).join(',')).join('\r\n'),'text/csv;charset=utf-8');
 }
 exportEmployees(){
   const rows:unknown[][]=[['الملف','نوع التقرير','الاسم','الرقم القومي','كود الجهة المؤسسي','رقم الصرف','البنك','الاستحقاقات','الاستقطاعات','الصافي','صفحة البداية','صفحة النهاية','حالة المراجعة']];
   for(const r of this.results)for(const e of r.employees)rows.push([r.fileName,r.kind,e.name,e.nationalId,e.institutionalEntityCode,e.disbursementNumber,e.bank,e.gross,e.deductions,e.net,e.startPage,e.endPage,e.warnings.join(' | ')]);
   this.csv('oracle-employees.csv',rows);
 }
 exportItems(){
   const rows:unknown[][]=[['الملف','الرقم القومي','الاسم','رمز الحساب','اسم البند','نوع البند','القيمة','الصفحة','النص الأصلي']];
   for(const r of this.results)for(const e of r.employees)for(const x of e.entries)rows.push([r.fileName,e.nationalId,e.name,x.accountCode,x.label,x.kind,x.amount,x.page,x.rawText]);
   this.csv('oracle-payroll-items.csv',rows);
 }
 exportJson(){this.save('oracle-import-review.json',JSON.stringify(this.results,null,2),'application/json;charset=utf-8')}
 sumEmployeeNet(r:Result){return r.employees.reduce((total,e)=>total+(e.net??0),0)}
 entryKindLabel(kind:string){return ({Earning:'استحقاق',Deduction:'استقطاع',Informational:'معلوماتي',Subtotal:'إجمالي فرعي'} as Record<string,string>)[kind]??kind}
 matches(e:Employee){const s=this.search.trim();return !s||[e.name,e.nationalId,e.institutionalEntityCode,e.disbursementNumber].some(x=>x?.includes(s))}
}