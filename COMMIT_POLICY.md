# Commit policy

A request to “commit” means commit only the necessary, reviewable project files for the requested change.

- Do not stage local credentials, `.env` files, private research specifications, generated datasets, runtime state, logs, caches, or build output.
- Preserve unrelated or pre-existing files unless the task explicitly makes them part of the deliverable.
- Prefer separate logical commits for production code, tests, and documentation/tooling.
- Before pushing, review `git status`, the staged file list, and the resulting diff.
