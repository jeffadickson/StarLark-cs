using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

// A static file server for the browser playground and the HTML docs, so nothing outside the .NET SDK is needed.
//
//   Serve <folder> [port]        e.g.  dotnet run --project tools/Serve -- docs/site 8765
//
// It serves files only from <folder>, on localhost only, with the content types WebAssembly needs (.wasm, .dat, .br and .gz
// framework files) and no caching, so a rebuilt playground is always picked up.

string folder = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
int port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 8765;
if (!Directory.Exists(folder)) { Console.Error.WriteLine($"no such folder: {folder}"); return 2; }

var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls($"http://localhost:{port}");
builder.Logging.ClearProviders();
var app = builder.Build();

var types = new FileExtensionContentTypeProvider();
types.Mappings[".wasm"] = "application/wasm";
types.Mappings[".dat"] = "application/octet-stream";
types.Mappings[".blat"] = "application/octet-stream";
types.Mappings[".webcil"] = "application/octet-stream";
types.Mappings[".star"] = "text/plain; charset=utf-8";
types.Mappings[".bas"] = "text/plain; charset=utf-8";

var files = new PhysicalFileProvider(folder);
app.Use(async (ctx, next) => { ctx.Response.Headers.CacheControl = "no-store"; await next(); });
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = types, ServeUnknownFileTypes = false });
Console.WriteLine($"serving {folder} on http://localhost:{port}");
app.Run();
return 0;
