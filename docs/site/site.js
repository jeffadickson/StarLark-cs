// Makes the documentation page live: loads the WebAssembly interpreter (playground/starlark.js), adds a Run button and an
// output panel to every .example, and drives the BASIC machine. Each example's data-options says which dialect features it
// needs; the playground takes its options from the checkboxes.

import { boot } from "./playground/starlark.js";

const status = document.getElementById("boot-status");
const examples = [...document.querySelectorAll(".example")];
const runs = [];   // one entry per example: () => Promise, used by the "run all" self check

// Build the toolbar and output panel first, so the page is complete even before the runtime has loaded.
for (const ex of examples) {
  const code = ex.querySelector(".code");
  const bar = document.createElement("div");
  bar.className = "bar";
  const run = document.createElement("button");
  run.type = "button";
  run.textContent = "Run";
  run.disabled = true;
  const reset = document.createElement("button");
  reset.type = "button";
  reset.className = "quiet";
  reset.textContent = "Reset";
  const flags = document.createElement("span");
  flags.className = "flags";
  const opts = ex.dataset.options || "";
  if (opts && !ex.classList.contains("playground")) flags.textContent = "dialect: " + opts;
  const meta = document.createElement("span");
  meta.className = "meta";
  bar.append(run, reset, flags, meta);
  const out = document.createElement("pre");
  out.className = "out";
  out.setAttribute("aria-live", "polite");
  ex.append(bar, out);
  const original = code.textContent;
  reset.addEventListener("click", () => { code.textContent = original; out.textContent = ""; meta.textContent = ""; });
  ex._parts = { code, run, out, meta };
}

function optionsFor(ex) {
  if (!ex.classList.contains("playground")) return ex.dataset.options || "";
  return [...document.querySelectorAll("#playground-options input:checked")].map(i => i.value).join(",");
}

function show(ex, result) {
  const { out, meta } = ex._parts;
  out.textContent = result.output;
  if (result.error) {
    const err = document.createElement("span");
    err.className = "err";
    err.textContent = result.error;
    out.append(err);
  }
  if (!result.output && !result.error) out.textContent = "(no output)";
  meta.textContent = `${result.ms.toFixed(1)} ms`;
}

let sl;
try {
  sl = await boot();
  status.textContent = "Interpreter loaded: examples run in your browser.";
  status.classList.add("ready");
  document.getElementById("version").textContent = sl.version();
} catch (e) {
  status.textContent = "The interpreter could not load in this browser: " + e;
  status.classList.add("failed");
  throw e;
}

for (const ex of examples) {
  const { code, run } = ex._parts;
  const go = () => {
    // innerText keeps the line breaks the reader typed; textContent would drop <br> elements a browser inserts.
    const source = code.innerText.replace(/ /g, " ");
    show(ex, sl.run(source, optionsFor(ex)));
  };
  run.disabled = false;
  run.addEventListener("click", go);
  code.addEventListener("keydown", e => {
    if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); go(); }
    if (e.key === "Tab") { e.preventDefault(); document.execCommand("insertText", false, "    "); }
  });
  runs.push(go);
}

// ---------------------------------------------------------------- the BASIC machine

const screen = document.getElementById("basic-screen");
const form = document.getElementById("basic-form");
const line = document.getElementById("basic-line");
const picker = document.getElementById("basic-program");
let timer = null;

function append(text) {
  // The machine sends a form feed for CLS.
  const at = text.lastIndexOf("\f");
  if (at >= 0) { screen.textContent = ""; text = text.slice(at + 1); }
  screen.textContent += text;
  if (screen.textContent.length > 60000) screen.textContent = screen.textContent.slice(-40000);
  screen.scrollTop = screen.scrollHeight;
}

function drive(result) {
  append(result.output);
  clearTimeout(timer);
  // "budget" means the program is still running: give it another slice soon, leaving the page responsive between slices.
  if (result.status === "budget") timer = setTimeout(() => drive(sl.basicContinue()), 0);
}

if (screen) {
  for (const name of sl.basicPrograms()) {
    const o = document.createElement("option");
    o.value = o.textContent = name;
    if (name === "sine") o.selected = true;
    picker.append(o);
  }
  append(sl.basicReset());
  form.addEventListener("submit", e => {
    e.preventDefault();
    const text = line.value.toUpperCase();
    line.value = "";
    drive(sl.basicType(text));
  });
  document.getElementById("basic-load").addEventListener("click", () => {
    clearTimeout(timer);
    screen.textContent = "";
    sl.basicLoad(picker.value);
    append("LOAD " + picker.value.toUpperCase() + "\n");
    drive(sl.basicType("RUN"));
    line.focus();
  });
  document.getElementById("basic-break").addEventListener("click", () => { sl.basicBreak(); drive(sl.basicContinue()); });
  document.getElementById("basic-reset").addEventListener("click", () => { clearTimeout(timer); screen.textContent = ""; append(sl.basicReset()); });
}

// ---------------------------------------------------------------- self check

// With ?selfcheck in the address, run every example once and compare what it shows with its data-expect attribute (text the
// output must contain; for an example that demonstrates an error, part of the error). The summary goes into the status line,
// so tools/WebCheck can confirm in a headless browser that every example on the page still does what the page says.
if (location.search.includes("selfcheck")) {
  const failures = [];
  for (let i = 0; i < runs.length; i++) {
    runs[i]();
    const text = examples[i]._parts.out.textContent;
    const want = examples[i].dataset.expect;
    if (want === undefined) failures.push(`example ${i + 1} has no data-expect`);
    else if (!text.includes(want)) failures.push(`example ${i + 1}: expected "${want}" in "${text.slice(0, 120)}"`);
  }
  const basicOk = screen && screen.textContent.includes("READY");
  if (!basicOk) failures.push("the BASIC machine did not show READY");
  status.textContent = `selfcheck: ${runs.length} examples, ${failures.length} problems` +
    (failures.length === 0 ? " - ALL PASSED" : " - FAILED: " + failures.join("; "));
}
