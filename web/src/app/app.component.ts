import { Component, inject } from '@angular/core';import { CommonModule } from '@angular/common';import { HttpClient } from '@angular/common/http';import { FormsModule } from '@angular/forms';
interface Entry {accountCode:string|null;label:string;amount:number;kind:string;page:number;rawText:string}
interface Employee {nationalId:string|null;disbursementNumber:string|null;name:string;bank:string|null;gross:number|null;deductions:number|null;net:number|null;entries:Entry[];warnings:string[];startPage:number;endPage:number}
interface Issue {code:string;message:string;page:number|null;severity:string}
interface Result {fileName:string;kind:string;status:string;pages:number;employees:Employee[];groups:unknown[];issues:Issue[]}
@Component({selector:'app-root',standalone:true,imports:[CommonModule,FormsModule],template:`
<main class="shell"><header><div class="brand">ORACLE <span>PDF PARSER</span></div><div class="subtitle">استيراد وتحليل تقارير المرتبات • بدون إحداثيات ثابتة</div></header>
<section class="hero"><div><h1>مراجعة كشوف الرواتب بدقة</h1><p>ارفع صرفية موظف أو كافر أو كشوف صرف. لن يتم اعتماد بيانات غير مؤكدة تلقائيًا.</p></div>
<label class="upload">اختر ملفات PDF<input aria-label="اختر ملفات PDF" type="file" accept=".pdf,application/pdf" multiple (change)="select($event)"></label><button (click)="upload()" [disabled]="busy||files.length===0">{{busy?'جارٍ التحليل...':'تحليل الملفات'}}</button><p class="small">{{files.length}} ملف محدد (الحد الأقصى 3)</p></section>
<p *ngIf="error" class="error" role="alert">{{error}}</p>
<section *ngFor="let report of results" class="panel"><div class="panelhead"><h2>{{report.fileName}}</h2><span class="tag">{{report.kind}} • {{report.status}}</span></div>
<div class="stats"><div><small>الصفحات</small><b>{{report.pages}}</b></div><div><small>الموظفون المستخرجون</small><b>{{report.employees.length}}</b></div><div><small>تحذيرات</small><b>{{report.issues.length}}</b></div></div>
<div *ngIf="report.issues.length" class="warning"><h3>تحتاج مراجعة</h3><p *ngFor="let issue of report.issues.slice(0,30)">{{issue.code}}: {{issue.message}} <span *ngIf="issue.page">— صفحة {{issue.page}}</span></p><p *ngIf="report.issues.length>30">و{{report.issues.length-30}} تحذير إضافي</p></div>
<div class="search"><label>بحث عن موظف</label><input [(ngModel)]="search" placeholder="الاسم أو الرقم القومي أو رقم الصرف"></div>
<div class="tablewrap"><table><thead><tr><th>الاسم</th><th>الرقم القومي</th><th>رقم الصرف</th><th>الصافي</th><th>الحالة</th><th>التفاصيل</th></tr></thead><tbody><ng-container *ngFor="let e of report.employees"><tr *ngIf="matches(e)"><td>{{e.name}}</td><td>{{e.nationalId||'—'}}</td><td>{{e.disbursementNumber||'—'}}</td><td>{{e.net ?? '—'}}</td><td>{{e.warnings.length?'مراجعة':'مكتمل'}}</td><td><button class="plain" (click)="selected=selected===e?null:e">عرض البنود</button></td></tr></ng-container></tbody></table></div>
<div class="details" *ngIf="selected as e"><h3>بنود {{e.name}}</h3><p>الصفحات {{e.startPage}}–{{e.endPage}} • الاستحقاقات: {{e.gross??'غير مقروء'}} • الاستقطاعات: {{e.deductions??'غير مقروء'}}</p><table><thead><tr><th>الحساب</th><th>البند</th><th>المبلغ</th><th>النوع</th></tr></thead><tbody><tr *ngFor="let item of e.entries"><td>{{item.accountCode}}</td><td>{{item.label}}</td><td>{{item.amount}}</td><td>{{item.kind}}</td></tr></tbody></table></div>
</section></main>`,styles:[``]})
export class AppComponent {private http=inject(HttpClient);files:File[]=[];busy=false;error='';results:Result[]=[];search='';selected:Employee|null=null;
 select(e:Event){this.files=Array.from((e.target as HTMLInputElement).files||[]).slice(0,3);this.error=''}
 upload(){if(!this.files.length)return;this.busy=true;this.error='';const form=new FormData();this.files.forEach(f=>form.append('files',f));this.http.post<Result[]>('/api/import',form).subscribe({next:r=>{this.results=r;this.busy=false},error:e=>{this.error=e.error?.error||'تعذر تحليل الملف';this.busy=false}})}
 matches(e:Employee){const s=this.search.trim();return !s||[e.name,e.nationalId,e.disbursementNumber].some(x=>x?.includes(s))}
}