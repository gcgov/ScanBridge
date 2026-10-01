# Deploy ScanBridge with Intune

The **Release** workflow uploads each signed installer to Microsoft Intune. Intune then
updates ScanBridge on the managed computers. This page explains the process and the
one-time setup.

## How the upload works

The `intune` job in `.github/workflows/release.yml` runs after the `release` job. It
does these steps:

1. Download the signed `ScanBridge-<version>-setup.exe` from the `release` job.
2. Copy it to a package folder as `ScanBridge-setup.exe`, next to
   `installer/intune/uninstall.cmd`.
3. Package the folder with the Microsoft Win32 Content Prep Tool.
4. Upload the package to the existing Intune app as a new content version.
5. Set the app version, the install command, the uninstall command, and the detection
   rule.

The detection rule checks the `DisplayVersion` value that the installer writes to the
registry. The rule requires the new version or later. A computer with an older version
fails detection, so Intune installs the new package on it. Intune keeps the app's
assignments, so you assign the app one time only.

The job skips the upload in these cases:

- The `INTUNE_APP_ID` or `INTUNE_CLIENT_ID` variable is not set.
- The tag is a prerelease, such as `v1.2.0-beta`. Intune compares versions as numbers
  only.

The script that does the upload is `.github/scripts/Publish-IntuneWin32App.ps1`.

## Identity and approval

The `intune` job keeps the Intune upload apart from code signing:

- It runs in the `intune` GitHub environment, not the `release` environment.
- It signs in as its own Microsoft Entra app, the *Intune publisher app*. This app can
  update Intune apps, but it cannot sign code. The signing app cannot change Intune.
- Required reviewers on the `intune` environment approve each upload. The `release` job
  builds, signs, and publishes the release first. The `intune` job then waits for
  approval.

Microsoft Graph cannot limit `DeviceManagementApps.ReadWrite.All` to specific Intune
apps. The permission covers every Intune app in the tenant. The approval step and the
environment rules limit when the workflow can use it.

## App settings

The workflow sets these values on every release. Changes that you make to them in the
Intune admin center last only until the next release.

| Setting | Value |
|---|---|
| Install command | `ScanBridge-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS` |
| Uninstall command | `uninstall.cmd` |
| Detection rule | Registry, `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9C2E6F0A-3B7D-4A1E-9F5C-7B8A2D4E6F10}_is1`, value `DisplayVersion`, version comparison, greater than or equal to the release version |

The installer installs ScanBridge for one user, in
`%LOCALAPPDATA%\Programs\ScanBridge`. For this reason, the app must use the **User**
install behavior. A silent install selects the **autostart** task, so ScanBridge starts
when the user signs in. A silent install also closes a running ScanBridge and then
starts it again. On a first install, ScanBridge opens its settings window.

## One-time setup

You need these roles:

- **Application Administrator** in Microsoft Entra, to create the Intune publisher app.
- **Privileged Role Administrator** or **Global Administrator** in Microsoft Entra, to
  grant admin consent.
- **Intune Administrator**, or an Intune role that can create and assign apps.
- The **Admin** role on the GitHub repository, to create the environment.

### 1. Create the Intune publisher app

PrintBridge, ScanBridge, and Tax Import Processor share one Intune publisher app. If
the app exists already, go to item 7 to add this repository.

1. In the [Microsoft Entra admin center](https://entra.microsoft.com), go to
   **Entra ID** > **App registrations** > **New registration**.
2. Set **Name** to `GitHub Intune publisher`.
3. Select **Accounts in this organizational directory only**, and then select
   **Register**.
4. Select **API permissions** > **Add a permission** > **Microsoft Graph** >
   **Application permissions**.
5. Select `DeviceManagementApps.ReadWrite.All`, and then select **Add permissions**.
6. Select **Grant admin consent for \<your tenant\>**.
7. Select **Certificates & secrets** > **Federated credentials** > **Add credential**.
8. Set **Federated credential scenario** to **GitHub Actions deploying Azure
   resources**, and set these values:
   - **Organization**: `gcgov`
   - **Repository**: `ScanBridge`
   - **Entity type**: **Environment**
   - **GitHub environment name**: `intune`
   - **Name**: `ScanBridge-intune`
9. Select **Add**.
10. On the **Overview** page, copy the **Application (client) ID**.

Do not add a client secret. The workflow signs in with OpenID Connect (OIDC), so GitHub
stores no secret. Do not give this app any Azure subscription role.

### 2. Create the Win32 app in Intune

Intune needs a first package to create the app. Make it on a Windows computer from the
latest release.

1. Download `IntuneWinAppUtil.exe` from the
   [Microsoft Win32 Content Prep Tool](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool)
   repository.
2. Download `ScanBridge-<version>-setup.exe` from the latest GitHub release.
3. Make the package with these PowerShell commands, from the repository root:

   ```powershell
   New-Item -ItemType Directory package
   Copy-Item ScanBridge-<version>-setup.exe package\ScanBridge-setup.exe
   Copy-Item installer\intune\uninstall.cmd package\
   .\IntuneWinAppUtil.exe -c package -s ScanBridge-setup.exe -o out -q
   ```

4. In the [Intune admin center](https://intune.microsoft.com), go to **Apps** >
   **Windows** > **Create**.
5. Select **Windows app (Win32)**, and then select **Select**.
6. Upload `out\ScanBridge-setup.intunewin`.
7. On the **App information** page, set **Publisher** to `Garrett County Government`.
8. On the **Program** page, set these values:
   - **Install command**: the install command from [App settings](#app-settings).
   - **Uninstall command**: `uninstall.cmd`.
   - **Install behavior**: **User**.
9. On the **Requirements** page, set **Operating system architecture** to **x64**.
   Set **Minimum operating system** to the oldest Windows version that you support.
10. On the **Detection rules** page, select **Manually configure detection rules**.
    Add the registry rule from [App settings](#app-settings).
11. On the **Assignments** page, add your groups under **Required**. Use user groups or
    device groups. Start with a small pilot group, then add the other groups.
12. Select **Create**.

### 3. Create the `intune` environment

1. In the Intune admin center, open the new app. The app ID is the GUID in the browser
   address, after `appId/`.
2. In GitHub, go to **Settings** > **Environments** > **New environment**.
3. Set **Name** to `intune`, and then select **Configure environment**.
4. Select **Required reviewers**. Add the people who approve deployments, and then
   select **Save protection rules**.
5. Under **Deployment branches and tags**, select **Selected branches and tags**.
6. Select **Add deployment branch or tag rule**. Set **Ref type** to **Tag**, set
   **Name pattern** to `v*`, and then select **Add rule**.
7. Under **Environment variables**, add these variables:

   | Variable | Value |
   |---|---|
   | `INTUNE_CLIENT_ID` | The client ID of the Intune publisher app, from step 1. |
   | `AZURE_TENANT_ID` | The Microsoft Entra tenant ID. Use the same value as in the `release` environment. |
   | `INTUNE_APP_ID` | The app ID, from item 1 of this step. |

The tag rule lets only `v*` tags use the environment. A workflow on another branch or
tag cannot sign in as the Intune publisher app.

To limit who can create `v*` tags, add a tag ruleset in **Settings** > **Rules** >
**Rulesets**.

## Check a release

1. Push a new tag, such as `v1.2.0`.
2. In the **Actions** tab, open the **Release** run. After the `release` job, select
   **Review deployments**. Select **intune**, and then select **Approve and deploy**.
3. Wait for the `intune` job to finish. The job ends with a line
   such as `Intune app 'ScanBridge' now deploys version 1.2.0`.
4. In the Intune admin center, open the app. The **Properties** page shows the new
   version and the new detection rule.
5. On a pilot computer, open **Company Portal** and select **Sync**. Intune installs the
   new version within a few minutes. Without a sync, the Intune Management Extension
   checks for apps about every 8 hours.
6. Open **Device install status** on the app to watch the other computers.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| The `intune` job waits | The job waits for a required reviewer. Select **Review deployments** on the run. |
| The `intune` job says it skipped the upload | Set `INTUNE_APP_ID` and `INTUNE_CLIENT_ID` in the `intune` environment. |
| `azure/login` fails in the `intune` job | The Intune publisher app has no federated credential for the `intune` environment of this repository, or `AZURE_TENANT_ID` is missing from the `intune` environment. |
| `403 Forbidden` from Microsoft Graph | The Intune publisher app has no `DeviceManagementApps.ReadWrite.All` permission, or no admin consent. Do step 1 again. |
| `404 Not Found` from Microsoft Graph | `INTUNE_APP_ID` does not match an app in the tenant. Check the value. |
| The install fails on a computer | Read `C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\AppWorkload.log` on that computer. |

To upload a release again, open its **Release** run in the **Actions** tab. Select
**Re-run jobs** > **Re-run failed jobs**, or re-run the `intune` job alone.
