using OraclePdfParser.Application;
using OraclePdfParser.Domain;
using OraclePdfParser.Infrastructure;

var builder=WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options=>options.Limits.MaxRequestBodySize=80_000_000);
builder.Services.AddCors(options=>options.AddPolicy("AngularDev",policy=>policy.WithOrigins("http://localhost:4200","http://127.0.0.1:4200").AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton<IPdfTextExtractor,PdfiumTextExtractor>();
builder.Services.AddSingleton<IPayrollImportRepository>(_=>new SqliteImportRepository(
    builder.Configuration["Data:SqlitePath"]??Path.Combine(builder.Environment.ContentRootPath,"data","imports.db")));
builder.Services.AddScoped<PayrollImportService>();
var app=builder.Build();
app.UseCors("AngularDev");
app.MapGet("/api/health",()=>Results.Ok(new{status="ok"}));
app.MapPost("/api/imports",async (HttpRequest request,PayrollImportService service,CancellationToken ct)=>
{
    if(!request.HasFormContentType) return Results.BadRequest(new{error="استخدم multipart/form-data"});
    var form=await request.ReadFormAsync(ct);
    if(form.Files.Count is <1 or >3) return Results.BadRequest(new{error="ارفع ملف PDF واحد إلى ثلاثة"});
    var uploads=new List<UploadSource>();
    foreach(var file in form.Files)
    {
        if(!Path.GetExtension(file.FileName).Equals(".pdf",StringComparison.OrdinalIgnoreCase) || file.Length>25_000_000 || file.Length==0)
            return Results.BadRequest(new{error="اسم الملف أو حجمه غير صالح"});
        using var ms=new MemoryStream();
        await file.CopyToAsync(ms,ct);
        uploads.Add(new UploadSource(Path.GetFileName(file.FileName),ms.ToArray()));
    }
    try {var batch=await service.ImportAsync(uploads,ct);return Results.Ok(batch);}
    catch(InvalidDataException ex){return Results.BadRequest(new{error=ex.Message});}
},new Microsoft.AspNetCore.Http.Metadata.ProducesResponseTypeMetadata(200,typeof(PayrollImport),["application/json"])); 
app.MapGet("/api/imports",async (IPayrollImportRepository repository,CancellationToken ct)=>
{
    var imports=await repository.ListAsync(ct);
    return Results.Ok(imports.Select(b=>new{b.Id,b.ImportedAt,employees=b.Employees.Count,payments=b.Payments.Count,covers=b.Covers.Count,blocking=b.HasBlockingErrors}));
});
app.MapGet("/api/imports/{id:guid}",async (Guid id,IPayrollImportRepository repository,CancellationToken ct)=>
    (await repository.GetAsync(id,ct)) is {} batch?Results.Ok(batch):Results.NotFound());
app.MapGet("/api/imports/{id:guid}/employees/{nationalId}",async (Guid id,string nationalId,IPayrollImportRepository repository,CancellationToken ct)=>
{
    var batch=await repository.GetAsync(id,ct);
    var found=batch?.Employees.FirstOrDefault(e=>e.NationalId==nationalId);
    return found is null?Results.NotFound():Results.Ok(found);
});
app.Run();
public partial class Program { }
