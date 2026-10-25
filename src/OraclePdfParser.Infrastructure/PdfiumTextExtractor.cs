using System.Runtime.InteropServices;
using System.Threading;
using OraclePdfParser.Application;
using OraclePdfParser.Domain;

namespace OraclePdfParser.Infrastructure;

/// <summary>Uses PDFium's intrinsic reading order; does NOT apply coordinates or a fixed layout grid.</summary>
public sealed class PdfiumTextExtractor : IPdfTextExtractor
{
    private static readonly Lazy<bool> Initialized = new(() => { Native.FPDF_InitLibrary(); return true; },LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly object Gate = new();
    public PdfDocumentText Extract(byte[] bytes)
    {
        if(bytes.Length<5 || bytes[0]!=37 || bytes[1]!=80 || bytes[2]!=68 || bytes[3]!=70 || bytes[4]!=45)
            throw new InvalidDataException("الملف لا يبدأ بتوقيع PDF صحيح");
        if(bytes.Length>25_000_000) throw new InvalidDataException("حجم الملف أكبر من 25 ميجابايت");
        _=Initialized.Value;
        lock(Gate)
        {
            var handle=GCHandle.Alloc(bytes,GCHandleType.Pinned);
            nint document=IntPtr.Zero;
            try
            {
                document=Native.FPDF_LoadMemDocument(handle.AddrOfPinnedObject(),bytes.Length,IntPtr.Zero);
                if(document==IntPtr.Zero) throw new InvalidDataException("تعذر فتح PDF (قد يكون مشفرًا أو تالفًا)");
                int pages=Native.FPDF_GetPageCount(document);
                if(pages<=0 || pages>2000) throw new InvalidDataException("عدد صفحات PDF غير مدعوم");
                var result=new List<PdfPageText>(pages);
                for(int index=0;index<pages;index++)
                {
                    nint page=Native.FPDF_LoadPage(document,index);
                    if(page==IntPtr.Zero) throw new InvalidDataException($"تعذر قراءة الصفحة {index+1}");
                    try
                    {
                        nint textPage=Native.FPDFText_LoadPage(page);
                        if(textPage==IntPtr.Zero) throw new InvalidDataException($"تعذر فتح نص الصفحة {index+1}");
                        try
                        {
                            int chars=Native.FPDFText_CountChars(textPage);
                            if(chars<0 || chars>2_000_000) throw new InvalidDataException("صفحة بها عدد أحرف غير متوقع");
                            var buffer=new char[chars+1];
                            int copied=Native.FPDFText_GetText(textPage,0,chars+1,buffer);
                            var text=copied>0?new string(buffer,0,Math.Min(copied,buffer.Length)).TrimEnd('\0'):"";
                            result.Add(new PdfPageText(index+1,text));
                        }
                        finally {Native.FPDFText_ClosePage(textPage);}
                    }
                    finally {Native.FPDF_ClosePage(page);}
                }
                return new PdfDocumentText(result);
            }
            finally
            {
                if(document!=IntPtr.Zero) Native.FPDF_CloseDocument(document);
                handle.Free();
            }
        }
    }

    private static class Native
    {
        [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] internal static extern void FPDF_InitLibrary();
        [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] internal static extern nint FPDF_LoadMemDocument(nint data,int size,nint password);
        [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] internal static extern void FPDF_CloseDocument(nint doc);
        [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDF_GetPageCount(nint doc);
        [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] internal static extern nint FPDF_LoadPage(nint doc,int index);
        [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] internal static extern void FPDF_ClosePage(nint page);
        [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] internal static extern nint FPDFText_LoadPage(nint page);
        [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] internal static extern int FPDFText_CountChars(nint textPage);
        [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)] internal static extern int FPDFText_GetText(nint textPage,int start,int count,[Out] char[] data);
        [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] internal static extern void FPDFText_ClosePage(nint textPage);
    }
}