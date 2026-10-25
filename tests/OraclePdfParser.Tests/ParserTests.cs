using System.Text;
using OraclePdfParser.Application;
using OraclePdfParser.Domain;
using OraclePdfParser.Infrastructure;
using Xunit;

namespace OraclePdfParser.Tests;

public sealed class ParserTests
{
    [Fact]
    public void ArabicText_ConvertsDigits_WithoutReversingText()
    {
        Assert.Equal("علاوة 2015 4.80",ArabicText.Normalize("علاوة ٢٠١٥ ٤٫٨٠"));
        Assert.Equal(3000.55m,ArabicText.LastMoney("بدل الجامعة ٣،٠٠٠٫٥٥"));
    }

    [Fact]
    public void Payslip_HandlesWrappedItem_AndEmployerShareAcrossPages()
    {
        var document=new PdfDocumentText([
            new PdfPageText(1, """
                نظام أوراكل للرواتب
                اسم الموظف: موظف اختباري رقم الصرف: ٣٠٢٠٠١٠٥-١٢٣٤٥ الرقم القومى: ٢٨٠١٠١٠٠٠٠٠٠٠١
                نوع الصرف: دفع الكترونى البنك: بنك مصر الفرع: مركز البطاقات
                الاستحقاقات
                الاسم القيمة الإجمالى
                ٢١١١٠٥١٢
                علاوة الحد الأدنى للحزمة
                ١٠٠ الاجتماعية
                ٢١١١٠١٠١ المرتب الاساسى ٢٠٠
                أجمالى الاستحقاقات ٣٠٠
                ٢١١٢٠٢٠٢ حكومة ١ اصابة عمل ٢٠
                الاستقطاعات
                الاسم القيمة الإجمالى
                التأمينات حصة الحكومة ٥٠ ٤١١١٠٢٠١
                الأجمالى الاستقطاعات ٥٠
                """),
            new PdfPageText(2, """
                نظام أوراكل للرواتب
                ٢١١٢٠١٠١
                حكومة ١٢ شيخوخة وعجز
                ٣٠ ووفاة
                الصافى: ٣٠٠
                هذا البيان للمراجعة فقط
                """)
        ]);
        var employee=Assert.Single(new PayslipParser().Parse(document));
        Assert.Equal("موظف اختباري",employee.Name);
        Assert.Equal("28010100000001",employee.NationalId);
        Assert.Equal(300m,employee.Earnings);
        Assert.Equal(50m,employee.Deductions);
        Assert.Equal(50m,employee.EmployerContributions);
        Assert.Equal(300m,employee.Net);
        Assert.Contains(employee.Items,i=>i.OriginalName.Contains("الحد الأدنى للحزمة")&&i.Amount==100m);
        Assert.DoesNotContain(employee.Issues,i=>i.Severity==IssueSeverity.Error);
    }

    [Fact]
    public void PercentageInLabel_IsNotMistakenForMoney()
    {
        var doc=new PdfDocumentText([new PdfPageText(1,"""
            اسم الموظف: موظف اختبار رقم الصرف: 30200105-12345 الرقم القومى: 28010100000001
            الاستحقاقات
            21110101 المرتب الاساسى 100
            أجمالى الاستحقاقات 100
            21120202 حكومة 0.25% اصابة عمل 40
            الاستقطاعات
            41110201 مستحقات 10
            الأجمالى الاستقطاعات 10
            الصافى: 130
            """)]);
        var employee=Assert.Single(new PayslipParser().Parse(doc));
        Assert.Equal(40m,employee.EmployerContributions);
        Assert.DoesNotContain(employee.Issues,i=>i.Severity==IssueSeverity.Error);
        Assert.Contains(employee.Items,i=>i.Kind==ItemKind.EmployerContribution && i.Amount==40m);
    }

    [Fact]
    public void Payslip_RejectsIncorrectNet()
    {
        var doc=new PdfDocumentText([new PdfPageText(1,"""
            اسم الموظف: موظف اختبار رقم الصرف: ٣٠٢٠٠١٠٥-١٢٣٤٥ الرقم القومى: ٢٨٠١٠١٠٠٠٠٠٠٠١
            الاستحقاقات
            ٢١١١٠١٠١ المرتب الاساسى ٢٠٠
            أجمالى الاستحقاقات ٢٠٠
            الاستقطاعات
            تأمينات ٢٠ ٤١١١٠٢٠١
            الأجمالى الاستقطاعات ٢٠
            الصافى: ١٩٠
            """)]);
        var employee=Assert.Single(new PayslipParser().Parse(doc));
        Assert.Contains(employee.Issues,i=>i.Code=="PAYSLIP_RECONCILIATION"&&i.Severity==IssueSeverity.Error);
    }

    [Fact]
    public void PaymentSheet_ParsesIdentityAndFinances()
    {
        var page=new PdfPageText(1,"""
            كشــــــــــــف صـــــــــــــــرف المرتبـــــــــــــــــات
            مجموعة التعيين: اختبار نوع صرف:دفع الكترونى البنك: بنك مصر الفرع:مركز البطاقات
            1
            8867-302001
            موظف اختبار 100.00 0.00 0.00 0.00 0.00 0.00 200.00 0.00 0.00 200.00 170.00 05
            270121802007
            97 30.00 0.00 0.00 0.00 0.00 10.00 20.00 0.00 0.00 0.00 0.00
            """);
        var issues=new List<ValidationIssue>();
        var entry=Assert.Single(new PaymentSheetParser().Parse(new PdfDocumentText([page]),issues));
        Assert.Equal("27012180200797",entry.NationalId);
        Assert.Equal("30200105-8867",entry.PaymentNumber);
        Assert.Equal(200m,entry.Earnings);
        Assert.Equal(30m,entry.Deductions);
        Assert.Equal(170m,entry.Net);
        Assert.DoesNotContain(issues,i=>i.Severity==IssueSeverity.Error);
    }

    [Fact]
    public void PaymentSheet_SupportsReversedAndCrossPageRowsAndSkipsGroupTotals()
    {
        var doc=new PdfDocumentText([
         new PdfPageText(1,"""
          كشف صرف المرتبات
          مجموعة التعيين: اختبار نوع صرف:دفع الكترونى البنك: بنك مصر الفرع: مركز البطاقات
          1
          8900-302001
          احمد اختبار 100.00 0.00 0.00 0.00 0.00 0.00 200.00 0.00 0.00 200.00 170.00 05
          270121802007
          """),
         new PdfPageText(2,"""
          97 30.00 0.00 0.00 0.00 0.00 10.00 20.00 0.00 0.00 0.00 0.00
          2
          8901-302001
          05
          موظف اخر
          180.00 200.00 0.00 0.00 190.00 0.00 0.00 0.00 100.00 0.00 90.00
          280121802007
          98 20.00 0.00 0.00 0.00 0.00 10.00 10.00 0.00 0.00 0.00 0.00
          إجمالــــــى السجل
          350.00 400.00 0.00 0.00 380.00 0.00 0.00 0.00 200.00 0.00 190.00
          50.00 0.00 0.00 0.00 0.00 20.00 30.00 0.00 0.00 0.00 0.00
          """)
        ]);
        var issues=new List<ValidationIssue>();
        var parsed=new PaymentSheetParser().Parse(doc,issues);
        Assert.Equal(2,parsed.Count);
        Assert.Equal(350m,parsed.Sum(e=>e.Net));
        Assert.Equal("27012180200797",parsed[0].NationalId);
        Assert.Equal("28012180200798",parsed[1].NationalId);
        Assert.DoesNotContain(issues,i=>i.Severity==IssueSeverity.Error);
    }

    [Fact]
    public void Cover_ParsesEndPageInsteadOfAssumingTwoPageReport()
    {
        var pages=new PdfDocumentText([
           new PdfPageText(1,"كشف إجماليات المرتب لكل سجل وبنك\nسجل رقم: اختبار نوع الصرف:دفع الكترونى\nالبنك : بنك مصر الفرع: مركز البطاقات\nالاستحقاقات"),
           new PdfPageText(2,"كشف إجماليات المرتب لكل سجل وبنك\nسجل رقم: اختبار نوع الصرف:دفع الكترونى\nالبنك : بنك مصر الفرع: مركز البطاقات\nالاستحقاقات"),
           new PdfPageText(3,"كشف إجماليات المرتب لكل سجل وبنك\nسجل رقم: اختبار نوع الصرف:دفع الكترونى\nالبنك : بنك مصر الفرع: مركز البطاقات\nجملة الصافى المستحق ١٧٠٫٠٠\nعدد العاملين: ١")
        ]);
        var issues=new List<ValidationIssue>();
        var cover=Assert.Single(new CoverParser().Parse(pages,issues));
        Assert.Equal(170m,cover.Net);Assert.Equal(1,cover.Workers);Assert.Equal(3,cover.Page);
    }

    [Fact]
    public void CrossValidation_DetectsContradictoryTotals()
    {
        var batch=new PayrollImport();
        batch.Payments.Add(new PaymentEntry("27012180200797","موظف","123",200,30,170,"بنك","فرع","مجموعة",1));
        batch.Covers.Add(new CoverEntry("بنك","فرع","مجموعة",1,160,2));
        PayrollImportService.Validate(batch);
        Assert.Contains(batch.Issues,i=>i.Code=="COVER_NET_MISMATCH");
    }

    [Fact]
    public void PdfiumSmoke_ExtractsTextFromSyntheticNativePdf()
    {
        var document=CreateTinyPdf();
        var parsed=new PdfiumTextExtractor().Extract(document);
        Assert.Contains("Hello PDFium",Assert.Single(parsed.Pages).Text);
    }
    private static byte[] CreateTinyPdf()
    {
        var s=new StringBuilder("%PDF-1.4\n");
        var offsets=new List<int>();
        void Add(string text){offsets.Add(Encoding.ASCII.GetByteCount(s.ToString()));s.Append(text);}
        Add("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Add("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Add("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        var body="BT /F1 12 Tf 72 720 Td (Hello PDFium) Tj ET\n";
        Add($"4 0 obj\n<< /Length {body.Length} >>\nstream\n{body}endstream\nendobj\n");
        Add("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        int xref=Encoding.ASCII.GetByteCount(s.ToString());
        s.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach(var offset in offsets) s.Append($"{offset:0000000000} 00000 n \n");
        s.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(s.ToString());
    }
}
