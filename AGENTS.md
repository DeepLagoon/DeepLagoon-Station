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
