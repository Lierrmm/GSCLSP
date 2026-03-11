// build.ts
await Bun.build({
  entrypoints: ["./src/extension.ts"],
  outdir: "./dist",
  target: "node",
  format: "cjs",
  external: ["vscode"],
  naming: "extension.js",
});

export {};
