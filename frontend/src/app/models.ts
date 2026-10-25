export type ReportType = 0|1|2|3;
export type IssueSeverity = 0|1|2;
export interface Issue { severity: IssueSeverity; code: string; message: string; page?: number; employeeId?: string }
export interface PayrollItem { accountCode?: string; originalName: string; amount: number; kind: 0|1|2|3; sourcePage: number; sourceText: string }
export interface Employee { nationalId:string; name:string; paymentNumber?:string; bank?:string; branch?:string; earnings?:number; deductions?:number; net?:number; employerContributions?:number; firstPage:number; lastPage:number; items:PayrollItem[]; issues:Issue[] }
export interface Payment { nationalId:string; name:string; paymentNumber?:string; earnings:number; deductions:number; net:number; bank:string; branch:string; group:string; page:number }
export interface Cover {bank:string; branch:string; group:string; workers:number; net:number;page:number }
export interface PayrollImport { id:string; importedAt:string; documents:{fileName:string;sha256:string;type:ReportType;pages:number}[]; employees:Employee[]; payments:Payment[]; covers:Cover[]; issues:Issue[]; netFromPayslips?:number; netFromPayments?:number; netFromCovers?:number; hasBlockingErrors:boolean }
