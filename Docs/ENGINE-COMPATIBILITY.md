# Official client compatibility

This branch prepares the signed official **292.0.1** client at
`26fc868a033836ae97389614f16298951b184df1`. This is a local release candidate,
not a statement about the deployed production version.

`ENGINE-COMPATIBILITY.json` pins the server engine to
`5a42b5f8e6705979ed28ad64a2ed4dc7de5b6269`. Its only differences from the official
client baseline are the server fixture quarantine implementation and tests.
The quarantine runs only on the server and changes no client implementation,
networked component schema, serialization, authentication, loader or module trust.
The exact five paths and Git blob IDs are recorded in the contract and separately
pinned in Control's `remote/engine_compatibility.py`.

This main merge preserves the exact source tree tested at
`4b3cf45060c80c2f0912b535fc5a2f17b75c956a`; only its Git history differs.
On 2026-10-21 the maintainer confirmed system functionality on the local Debug
server with the accompanying scalar-to-sequence YAML migration. This confirmation
does not independently establish a FullRelease TGUI run or a long-session test.

Engine changes are authored in the standalone RobustToolbox repository. Station
only consumes its pinned commit. No content build task may rewrite engine source.
Do not replace official client DLLs, relabel a custom client, or bypass signatures,
module selection or `insecure` flags. Historical experimental client branches are
not release inputs.

The official manifest redirects insecure `292.0.0` to `292.0.1`; use the exact
latter version. The selected signed `Robust.Client.WebView` is **288.1.0** for
Windows/Linux x64. Module versions identify their minimum supported engine.
Check the live official manifests again before a release. Verify SHA-256,
Ed25519, ZIP integrity and archive contents separately from runtime behavior.
An ImageSharp entry in the module's `.deps.json` does not prove that its DLL must
be inside that module ZIP; the verified official engine archives also contain it.

The migration uses native `ProtoId<T>` / `EntProtoId` data fields, by-ref BUI
attempt handlers, transform system anchoring and client render-transform APIs.
Grid splits now emit normal unanchor/anchor events; content must invalidate old
and new grid state through those events.

## Build and release checks

Run `python Tools/check_engine_compatibility.py` before building. It checks the
exact gitlink and engine HEAD, clean nested dependencies, the version, every
engine difference and its reviewed blob, and known fork-only content APIs.
The MSBuild target runs this check for client, server and packaging builds.
The publisher scripts use the pinned official version instead of the nearest
Git tag. Packaging repeats validation, including when started with
`dotnet run --no-build`, and embeds `engine-compatibility.json` in client/server
archives. This records the official client version/SHA and separate server SHA.

The packager's `--skip-build` is rejected: preexisting content DLLs have no
verified build provenance. Rebuild without that argument. `dotnet run
--no-build` remains supported and only skips rebuilding the packaging tool;
the packaging tool still rebuilds game content.

CI first validates the approved fork, then uses a separate job checkout at the
exact official engine commit to compile release content and run sandbox/API and
JSON-parser tests. `DeepLagoonStockValidation=true` is only for this exact stock
revision; it cannot generate release metadata. Ordinary release builds must use
the pinned approved source. Do not update either checkout during a build.

The content bridge uses the stock GET/chunk API. JSON actions are parsed by a
strict bounded content parser using sandbox-supported primitives; no custom
WebView JSON or POST API is referenced. Stock modules close/recreate hidden
browsers. Content shutdown detaches registered browsers before engine CEF
shutdown, avoiding a second close during later entity/UI teardown.

## Release acceptance

After the automated checks, use a clean official launcher against a test slot:
login, lobby/TGUI, spawn, movement, inventory, network state updates, reconnect
and official authentication. Check hub visibility and the exact advertised
`engine_version` in `/info` and `build.json`. A local compile or sandbox check
does not establish those live outcomes. Keep existing production state until
the isolated slot passes these checks.

The official CEF module itself still has `NoSandbox=true`. Origin allowlisting
does not isolate a compromised remote wiki. Prefer local sanitized guide data
or the system browser for remote pages; fixing CEF isolation belongs upstream
and must not introduce a custom-client requirement into production.
