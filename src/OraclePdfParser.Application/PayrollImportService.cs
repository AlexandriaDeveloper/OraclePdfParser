using System.Security.Cryptography;
using OraclePdfParser.Domain;

namespace OraclePdfParser.Application;

public sealed class PayrollImportService
{
    private readonly IPdfTextExtractor _extractor;
    private readonly IPayrollImportRepository _repository;
    public PayrollImportService(IPdfTextExtractor extractor,IPayrollImportRepository repository)
    { _extractor=extractor;_repository=repository; }

    public static ReportType DetectType(PdfDocumentText document)
    {
        string sample=ArabicText.Key(string.Join(" ",document.Pages.Take(3).Select(p=>p.Text)));
        if(sample.Contains(ArabicText.Key("كشف إجماليات المرتب لكل سجل وبنك"))) return ReportType.Cover;
        if(sample.Contains(ArabicText.Key("كشف صرف المرتبات"))) return ReportType.PaymentSheet;
        if(sample.Contains(ArabicText.Key("اسم الموظف")) || sample.Contains(ArabicText.Key("كشف رواتب"))) return ReportType.EmployeePayslip;
        return ReportType.Unknown;
    }
    public async Task<PayrollImport> ImportAsync(IReadOnlyList<UploadSource> sources,CancellationToken cancellationToken=default)
    {
        if(sources.Count is <1 or >3) throw new ArgumentException("يُسمح بملف PDF واحد إلى ثلاثة ملفات");
        var batch=new PayrollImport();
        var unique=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(source.Bytes.Length==0) throw new InvalidDataException("ملف PDF فارغ");
            var hash=Convert.ToHexString(SHA256.HashData(source.Bytes));
            if(!unique.Add(hash)) {batch.Issues.Add(new(IssueSeverity.Warning,"DUPLICATE_FILE","تم تجاهل ملف مكرر"));continue;}
            var document=_extractor.Extract(source.Bytes);
            var type=DetectType(document);
            batch.Documents.Add(new ImportedDocument(Path.GetFileName(source.Name),hash,type,document.Pages.Count));
            switch(type)
            {
                case ReportType.EmployeePayslip:
                    batch.Employees.AddRange(new PayslipParser().Parse(document));break;
                case ReportType.PaymentSheet:
                    batch.Payments.AddRange(new PaymentSheetParser().Parse(document,batch.Issues));break;
                case ReportType.Cover:
                    batch.Covers.AddRange(new CoverParser().Parse(document,batch.Issues));break;
                default:
                    batch.Issues.Add(new(IssueSeverity.Error,"UNKNOWN_DOCUMENT","نوع التقرير غير معروف"));break;
            }
        }
        Validate(batch);
        await _repository.SaveAsync(batch,cancellationToken);
        return batch;
    }
    public static void Validate(PayrollImport batch)
    {
        static bool Near(decimal x,decimal y)=>Math.Abs(x-y)<=.02m;
        var duplicateIds=batch.Employees.Where(e=>e.NationalId.Length==14)
            .GroupBy(e=>e.NationalId).Where(g=>g.Count()>1);
        foreach(var duplicates in duplicateIds)
            batch.Issues.Add(new(IssueSeverity.Error,"DUPLICATE_EMPLOYEE","تكرر الرقم القومي داخل صرفية الموظف",null,duplicates.Key));
        foreach(var e in batch.Employees)
        {
            if(e.NationalId.Length!=14 || !e.NationalId.All(char.IsDigit))
                e.Issues.Add(new(IssueSeverity.Error,"INVALID_NATIONAL_ID","رقم قومي غير صحيح الشكل",e.FirstPage));
            if(string.IsNullOrWhiteSpace(e.Name))
                e.Issues.Add(new(IssueSeverity.Error,"MISSING_NAME","اسم الموظف مفقود",e.FirstPage,e.NationalId));
        }
        if(batch.Employees.Count>0 && batch.Payments.Count>0)
        {
            if(batch.Employees.Count!=batch.Payments.Count)
                batch.Issues.Add(new(IssueSeverity.Error,"COUNT_MISMATCH","اختلاف عدد الموظفين بين صرفية موظف وكشوف الصرف"));
            var paymentIndex=batch.Payments.Where(p=>p.NationalId.Length==14).GroupBy(p=>p.NationalId).ToDictionary(x=>x.Key,x=>x.ToList());
            foreach(var employee in batch.Employees)
            {
                if(!paymentIndex.TryGetValue(employee.NationalId,out var payments))
                    employee.Issues.Add(new(IssueSeverity.Error,"MISSING_PAYMENT_MATCH","الموظف غير موجود في كشوف الصرف",employee.FirstPage,employee.NationalId));
                else if(payments.Count!=1)
                    employee.Issues.Add(new(IssueSeverity.Error,"DUPLICATE_PAYMENT_MATCH","أكثر من حركة صرف بنفس الرقم القومي",employee.FirstPage,employee.NationalId));
                else if(employee.Net is decimal net && !Near(net,payments[0].Net))
                    employee.Issues.Add(new(IssueSeverity.Error,"NET_EMPLOYEE_MISMATCH","صافي الموظف مختلف بين التقريرين",employee.FirstPage,employee.NationalId));
            }
        }
        if(batch.Covers.Count>0 && batch.Payments.Count>0)
        {
            if(batch.Covers.Sum(c=>c.Workers)!=batch.Payments.Count)
                batch.Issues.Add(new(IssueSeverity.Error,"COVER_WORKER_MISMATCH","عدد العاملين في الكافر لا يطابق كشف الصرف"));
            if(!Near(batch.Covers.Sum(c=>c.Net),batch.Payments.Sum(p=>p.Net)))
                batch.Issues.Add(new(IssueSeverity.Error,"COVER_NET_MISMATCH","إجمالي الصافي في الكافر لا يطابق كشف الصرف"));
        }
        if(batch.Employees.Count>0 && batch.Payments.Count>0 && batch.Employees.All(e=>e.Net.HasValue)
            && !Near(batch.Employees.Sum(e=>e.Net!.Value),batch.Payments.Sum(p=>p.Net)))
                batch.Issues.Add(new(IssueSeverity.Error,"OVERALL_NET_MISMATCH","اختلاف إجمالي صافي المرتبات"));
        if(batch.Employees.Count==0 && batch.Payments.Count==0 && batch.Covers.Count==0)
            batch.Issues.Add(new(IssueSeverity.Error,"EMPTY_IMPORT","لم يتم استخراج سجلات مالية"));
    }
}