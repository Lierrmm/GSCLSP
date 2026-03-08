// build.ts
await Bun.build({
  entrypoints: ["./src/extension.ts"],
  outdir: "./dist",
  target: "node",
  format: "cjs",
  // This is the important part:
  external: ["vscode"],
  naming: "extension.js",
});
