using System.Runtime.InteropServices;
using OraclePdfParser.Application;
namespace OraclePdfParser.Infrastructure;

/// <summary>Uses native PDFium's Unicode text stream; no character boxes or page coordinates.</summary>
public sealed class PdfiumTextSource:IPdfTextSource {
  private static readonly object Sync=new();
  [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] private static extern void FPDF_InitLibrary();
  [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] private static extern void FPDF_DestroyLibrary();
  [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr FPDF_LoadMemDocument(IntPtr buffer,int size,IntPtr password);
  [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] private static extern void FPDF_CloseDocument(IntPtr doc);
  [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] private static extern int FPDF_GetPageCount(IntPtr doc);
  [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr FPDF_LoadPage(IntPtr doc,int page);
  [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] private static extern void FPDF_ClosePage(IntPtr page);
  [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr FPDFText_LoadPage(IntPtr page);
  [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] private static extern void FPDFText_ClosePage(IntPtr textPage);
  [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] private static extern int FPDFText_CountChars(IntPtr textPage);
  [DllImport("pdfium",CallingConvention=CallingConvention.Cdecl)] private static extern int FPDFText_GetText(IntPtr textPage,int start,int length,IntPtr buffer);
  public IReadOnlyList<string> ReadPages(Stream stream){
    using var ms=new MemoryStream();stream.CopyTo(ms);
    var bytes=ms.ToArray();
    if(bytes.Length<5 || bytes.Length>30*1024*1024)throw new InvalidDataException("Invalid PDF size");
    var pinned=GCHandle.Alloc(bytes,GCHandleType.Pinned);
    try{
      lock(Sync){
        FPDF_InitLibrary();
        try{
          var doc=FPDF_LoadMemDocument(pinned.AddrOfPinnedObject(),bytes.Length,IntPtr.Zero);
          if(doc==IntPtr.Zero)throw new InvalidDataException("Cannot load PDF");
          try{
            var count=FPDF_GetPageCount(doc);
            if(count is <=0 or >10000)throw new InvalidDataException("Unsupported PDF page count");
            var result=new List<string>(count);
            for(int i=0;i<count;i++){
              var page=FPDF_LoadPage(doc,i);if(page==IntPtr.Zero)throw new InvalidDataException("Unreadable PDF page");
              try{
                var textPage=FPDFText_LoadPage(page);if(textPage==IntPtr.Zero)throw new InvalidDataException("Unable to extract Unicode");
                try{
                  var chars=FPDFText_CountChars(textPage);
                  if(chars<0||chars>2_000_000)throw new InvalidDataException("Unreasonable text length");
                  var buffer=Marshal.AllocHGlobal((chars+1)*2);
                  try{
                    var read=FPDFText_GetText(textPage,0,chars,buffer);
                    result.Add(read>1?Marshal.PtrToStringUni(buffer,read-1)??"":string.Empty);
                  }finally{Marshal.FreeHGlobal(buffer);}
                }finally{FPDFText_ClosePage(textPage);}
              }finally{FPDF_ClosePage(page);}
            }
            return result;
          }finally{FPDF_CloseDocument(doc);}
        }finally{FPDF_DestroyLibrary();}
      }
    }finally{pinned.Free();}
  }
}