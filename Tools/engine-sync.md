# Automatic engine updates

Push engine fixes to `main` in `DeepLagoon/RobustToolbox`. The Station workflow
`Sync DeepLagoon engine` checks that branch every five minutes and can also be
started manually in Actions. GitHub can delay scheduled runs. No cross-repository
token or engine hook is needed. Enable Actions/scheduled workflows if disabled
for the fork; the workflow needs permission to push to Station `main`.

The workflow accepts only a fast-forward of the existing engine revision, checks
the reviewed server/shared paths against the official release, verifies the
official launcher-selected signed WebView, compiles the Release client and server,
and runs the existing sandbox/JSON tests against the official engine source.
Only then does it commit and push the Station engine gitlink. It never forces a
push: concurrent Station changes are picked up by the next run. An unchanged
engine skips the builds. No running server is deployed or restarted.

Ordinary fork commits keep the official engine version (currently `292.0.1`).
The version comes from `MSBuild/Robust.Engine.Version.props` and must match
`ENGINE-COMPATIBILITY.json`; there are no revision hashes in that contract.
The Station gitlink still records the exact source revision for reproducibility.
Control already accepts another reviewed revision of the same official version,
so there is no per-commit agent policy or root image pin to update.

New files outside the reviewed paths, a new official release, changes to signed
modules, or new build dependencies still require the corresponding compatibility
review/cache preparation. A source commit alone cannot demonstrate real TGUI or
official launcher compatibility. The sync does not expand its own allowlist or
alter loader trust to pass a failing check.

GitHub does not trigger another push workflow for a commit made with
`GITHUB_TOKEN`, so the sync performs its validation before publishing. See
[GitHub event documentation](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows)
and [triggering workflows with tokens](https://docs.github.com/en/actions/how-tos/writing-workflows/choosing-when-your-workflow-runs/triggering-a-workflow).
