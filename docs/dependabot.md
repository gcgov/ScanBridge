# Automatic dependency updates

Dependabot keeps the NuGet packages and the GitHub Actions up to date. Minor and patch
updates go to Intune-managed computers without manual steps. This page explains the
process and the one-time setup.

## How an update reaches Intune

1. Every Monday, Dependabot opens pull requests for new package and action versions.
   Minor and patch updates come in one grouped pull request per ecosystem.
2. The **Dependabot auto-merge** workflow turns on auto-merge for a pull request with a
   minor or a patch update. This includes security updates.
3. GitHub merges the pull request after the **CI** check passes.
4. On weekdays at 17:23 UTC, the **Dependency release** workflow looks at `main`. If
   every commit since the last release comes from Dependabot, the workflow pushes the
   next patch tag. For example, `v1.1.0` becomes `v1.1.1`.
5. The workflow starts the **Release** workflow on the new tag. The **Release** workflow
   builds, signs, and publishes the release. Then it uploads the installer to Intune.
   See [intune.md](intune.md).

A major update waits for a person. Review it, and then merge it by hand. The next
**Dependency release** run releases it.

When `main` holds changes from people since the last release, the **Dependency
release** workflow does nothing. Push the next tag by hand. The release includes the
dependency updates too.

To release at once, open **Dependency release** in the **Actions** tab, and then select
**Run workflow**.

## One-time setup

Do these steps in the repository settings.

1. In **Settings** > **General** > **Pull Requests**, select **Allow auto-merge**.
2. In **Settings** > **Rules** > **Rulesets**, add a branch ruleset for `main`.
3. In the ruleset, select **Require status checks to pass**.
4. Add the `build` check from the **CI** workflow.
5. In **Settings** > **Advanced Security**, turn on **Dependabot alerts**.
6. In the same page, turn on **Dependabot security updates**.

Without the required `build` check, GitHub merges a pull request before CI finishes.

These settings also affect the automatic releases:

- If a tag ruleset limits who can create `v*` tags, add **GitHub Actions** to the
  bypass list. Otherwise the **Dependency release** workflow cannot push the tag.
- If the `intune` environment has required reviewers, each automatic release waits for
  approval before the Intune upload. Select **Review deployments** on the **Release**
  run.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| The auto-merge workflow fails with `Auto merge is not allowed` | Do step 1 of the setup. |
| A pull request merges before CI finishes | Do steps 2 to 4 of the setup. |
| The **Dependency release** run says that `main` has changes from people | Push the next tag by hand. |
| `git push` fails in the **Dependency release** run | A tag ruleset blocks GitHub Actions. Add it to the bypass list. |
| No **Release** run starts after the tag | The **Release** workflow file on the tag has no `workflow_dispatch` trigger. Run **Release** by hand on the tag. |
