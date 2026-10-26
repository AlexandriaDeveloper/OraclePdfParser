using OraclePdfParser.Application;
using OraclePdfParser.Domain;
using Xunit;

namespace OraclePdfParser.Tests;

public sealed class PayslipHeaderMetadataTests {
    [Fact]
    public void InstitutionalEntityCodeAndNationalIdCannotBecomeInformationalLineItems() {
        // Anonymized shape of the real Oracle payslip: source header contains
        // institutional entity code, disbursement suffix, 14-digit ID, and metadata.
        var page1 = """
            نظام أوراكل للرواتب
            المرتب عن شهر: أكتوبر سنــــــة ٢٠٢٦
            BR_٣٠٢٠٠١٠٥
            اسم الموظف: موظف تجريبي محمد علي رقم الصرف: ٣٠٢٠٠١٠٥-٠٠٠٠٠ الرقم القومى: ٠٠٠٠٠٠٠٠٠٠٠٠٠٠
            نوع الصرف: دفع الكترونى البنك: بنك مصر الفرع: مركز البطاقات
            الرقم التامينى: ٠٠١٢٣٤٥٦ الدرجة: مدير عام الادارة: كلية الطب - هيئة التدريس والهيئة المعاونة
            الاستحقاقات
            ٢١١١٠٤٤٢ بدل الجامعة ١٠٠٫٠٠
            أجمالى الاستحقاقات ١٠٠٫٠٠
            ٢١١٢٠٢٠٢ حكومة ١ اصابة عمل ٥٫٠٠
            ٢١١٢٠٢٠٢ حكومة ٠٫٢٥% اصابة عمل ٥٫٠٠
            ٢١١٢٠٢٠١ حكومة ٣ تامين صحى ٥٫٠٠
            الاستقطاعات
            ٤١١١٠٢٠١ مؤمن ٩ شيخوخة ٤٠٫٠٠
            الأجمالى الاستقطاعات ٤٠٫٠٠
            """;
        var page2 = """
            نظام أوراكل للرواتب
            BR_٣٠٢٠٠١٠٥
            ٢١١٢٠١٠١ حكومة ١٢ شيخوخة ٥٫٠٠
            الصافى: ٨٠٫٠٠
            هذا البيان للمراجعة فقط
            """;
        var report=new PayrollImportService().Import("dummy.pdf",[page1,page2]);
        var employee=Assert.Single(report.Employees);
        Assert.Equal("30200105",employee.InstitutionalEntityCode);
        Assert.Equal("30200105-00000",employee.DisbursementNumber);
        Assert.Equal("00000000000000",employee.NationalId);
        Assert.Equal(VerificationStatus.Verified,report.Status);
        Assert.Equal(100m,employee.Entries.Where(e=>e.Kind==EntryKind.Earning).Sum(e=>e.Amount));
        Assert.Equal(40m,employee.Entries.Where(e=>e.Kind==EntryKind.Deduction).Sum(e=>e.Amount));
        var contributions=employee.Entries.Where(e=>e.Kind==EntryKind.Informational).ToArray();
        Assert.Equal(4,contributions.Length);
        Assert.All(contributions,e=>Assert.Contains("حكومة",e.Label));
        Assert.All(employee.Entries,e=>{
            Assert.NotEqual("30200105",e.AccountCode);
            Assert.DoesNotContain("00000000000000",e.RawText);
            Assert.DoesNotContain("مدير عام",e.RawText);
        });
    }

    [Fact]
    public void RepeatedMetadataAfterSummaryCannotBeInterpretedAsAnEntry() {
        var pages=new[]{
            "نظام أوراكل للرواتب\nاسم الموظف: موظف تجريبي محمد علي رقم الصرف: 30200105-00000 الرقم القومى: 00000000000000\nالاستحقاقات\n21110442 بدل الجامعة 100.00\nاجمالي الاستحقاقات 100.00\nالاستقطاعات\n41110201 مؤمن 40.00\nاجمالي الاستقطاعات 40.00",
            "رقم الصرف: 30200105-00000 الرقم القومى: 00000000000000\n21120202 حكومة 1 اصابة عمل 5.00\n21120202 حكومة 0.25% اصابة عمل 5.00\n21120201 حكومة 3 تأمين صحي 5.00\n21120101 حكومة 12 شيخوخة 5.00\nالصافى: 80.00"
        };
        var report=new PayrollImportService().Import("metadata-continuation.pdf",pages);
        var employee=Assert.Single(report.Employees);
        Assert.Equal(VerificationStatus.Verified,report.Status);
        Assert.DoesNotContain(employee.Entries,e=>e.AccountCode=="30200105");
        Assert.Equal(4,employee.Entries.Count(e=>e.Kind==EntryKind.Informational));
    }
}
