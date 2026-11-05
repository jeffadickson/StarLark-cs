// A small wrapper around the WebAssembly build of starlark-cs. Pages import `boot` and get an object whose methods call the C#
// exports in web/Playground/Program.cs. Results that the C# side returns as JSON text are parsed here.
//
//   import { boot } from "./starlark.js";
//   const sl = await boot();
//   const r = sl.run("print(1 + 2)", "futures,types");   // { output, error, ms, steps }

import { dotnet } from "./_framework/dotnet.js";

let cached = null;

/** Loads the .NET runtime and the Starlark assemblies once, and returns the API object. */
export async function boot() {
  if (cached) return cached;
  const runtime = await dotnet.withApplicationArguments().create();
  const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
  const pg = exports.Playground;
  cached = {
    /** Runs a Starlark program. `options` is a comma-separated list: types, fstrings, futures, recursion, utf16, check. */
    run: (source, options = "") => JSON.parse(pg.Run(source, options)),
    version: () => pg.Version(),
    basicReset: () => pg.BasicReset(),
    basicType: (line) => JSON.parse(pg.BasicType(line)),
    basicContinue: () => JSON.parse(pg.BasicContinue()),
    basicBreak: () => pg.BasicBreak(),
    basicLoad: (name) => pg.BasicLoad(name),
    basicPrograms: () => pg.BasicPrograms().split("\n").filter(s => s.length > 0),
  };
  return cached;
}
