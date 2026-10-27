# Local files created during agent work

Store one-off diagnostics, inspection scripts, mockup generators, mockups,
image-generation prompts, screenshots, inventories, execution reports, exports
and snapshots in `.local/codex/<task>/`. This directory is ignored by Git and
must remain local. Keep credentials and private data out of tracked files.

Do not create these temporary files in source, resource or documentation
directories. Do not force-add ignored files. Before finishing, inspect
`git status --short --untracked-files=all` and the changed file list for local
artifacts. Use `git check-ignore -v --no-index <path>` to verify new scratch paths.

Keep reusable build/CI tools, regression tests and their required fixtures,
resource importers and required source assets, production services, shipped
resources, licenses and attribution tracked. A file is not temporary just
because an agent created it. Check references before excluding an existing
file, and preserve local contents when removing artifacts from the Git index.

# Station code and client compatibility

- Keep gameplay and content changes in this repository. Make engine changes in
  the separate RobustToolbox repository, then update the Station submodule to
  the reviewed engine commit. Do not patch engine files inside the Station
  checkout or copy engine implementations into content code as a workaround.
- Preserve compatibility with the official client, launcher and hub. Before
  changing the engine pin, client manifest, shared protocol, authentication or
  WebView/TGUI references, identify the exact official engine and signed module
  versions used by the release. Never bypass an `insecure` designation, module
  signature check or version selection rule, and never claim that a locally
  built client module is equivalent to the official signed module.
- Treat `Content.Client` and client-loaded `Content.Shared` assemblies as
  sandboxed code. Compilation alone is insufficient: avoid APIs and IL patterns
  rejected by RobustToolbox's client type checker. After changing either
  assembly, rebuild the actual client output and run
  `Content.Tests.Client.ClientSandboxTest.ClientModulesPassEngineTypeChecks`
  against those rebuilt assemblies and their runtime dependencies. Where an
  installed official client engine is available, also run
  `ClientModulePassesInstalledLauncherEngineTypeChecks` with
  `SS14_LAUNCHER_ENGINE_DIR` set to its engine DLL directory. Report skipped or
  unavailable checks explicitly; do not treat them as passes.
- For an engine or WebView/TGUI migration, validate the packaged `FullRelease`
  client and server, not just a Debug build. Check the client manifest, exact
  engine version and signed WebView reference, then test login and a real TGUI
  window with the official client. Test connection stability separately without
  opening TGUI so network timeouts are not attributed to the browser without
  evidence. State clearly which checks were automated and which need in-game
  confirmation.
