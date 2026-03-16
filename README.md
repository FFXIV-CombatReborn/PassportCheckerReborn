# [![](https://raw.githubusercontent.com/FFXIV-CombatReborn/RebornAssets/main/IconAssets/PCR_Icon.png)](https://github.com/FFXIV-CombatReborn/PassportCheckerReborn)

**Passport Checker Reborn**

![Github Latest Releases](https://img.shields.io/github/downloads/FFXIV-CombatReborn/PassportCheckerReborn/latest/total.svg?style=for-the-badge)
![Github License](https://img.shields.io/github/license/FFXIV-CombatReborn/PassportCheckerReborn.svg?label=License&style=for-the-badge)
[![](https://dcbadge.limes.pink/api/server/p54TZMPnC9)](https://discord.gg/p54TZMPnC9)

An open-source Party Finder enhancement plugin for Final Fantasy XIV, built on the [Dalamud](https://github.com/goatcorp/Dalamud) plugin framework. Passport Checker Reborn shows a member-info overlay alongside Party Finder listings and integrates with [FFLogs](https://www.fflogs.com/) and [Tomestone.gg](https://tomestone.gg/) for quick player lookup.

## Features

### Party Finder Overlay
- **Member Info Overlay** — automatically opens alongside the PF detail pane showing party members' names, jobs, and icons.
- **FFLogs Integration** — on-demand lookup of per-job parse percentiles with colour-coded results (grey → green → blue → purple → orange).
- **Tomestone.gg Integration** — on-demand prog-point and clear data for the current duty from the Tomestone API.
- **Overlay Positioning** — attach the overlay to the left or right side of the PF detail window.
- **High-End Duty Filter** — optionally limit the overlay to Savage, Ultimate, Extreme, Criterion, and Unreal duties.
- **Special Border for Known Players** — highlight party members you know with a configurable coloured border.
- **Party Job Icons** — display in-game job icons next to each member, with a text fallback.

### Party List Overlay
- **Current Party Info** — a separate overlay attached to the in-game Party Members list showing FFLogs and Tomestone data for your party.
- **Configurable Position** — Left, Right, Above, Below, or Unbound (free-floating).
- **Duty Selector** — choose a specific encounter for per-party lookups.
- **Auto-Hide** — optionally hide the party list overlay while in duty or combat.
- **Cross-World Support** — detects cross-world parties via `InfoProxyCrossRealm`.

### Party Finder List Enhancements
- **Auto-Refresh** — periodically refreshes the PF listing at a configurable interval (10–120 seconds), pausing while the detail pane is open.

## Commands

| Command | Description |
|---|---|
| `/pfchecker` | Toggle the main plugin window |

## Installation

- Enter `/xlsettings` in the chat window and go to the Experimental tab in the opening window.
- **Skip below the DevPlugins section to the Custom Plugin Repositories section.**
- Copy and paste the repo.json link into the first free text input field.
```
https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json
```
- Click on the + button and make sure the checkmark beside the new field is set afterwards.
- **Click on the Save-icon in the bottom right.**

## Configuration

Open settings with `/pfcheckersettings` or via the Dalamud plugin installer.

### General Tab
Configure Party Finder detail and list enhancements. Some settings are placeholders for future features and are shown as disabled in the UI.

### Overlay Tab
Toggle the member info overlay, high-end duty filter, overlay side, and FFLogs/Tomestone integrations. Configure the party list overlay position and auto-hide behaviour.

### FFLogs Integration Tab
Enter your FFLogs API Client ID and Client Secret, then click **Save & Test Credentials** to verify.

<details>
<summary>How to obtain FFLogs API credentials</summary>

1. Go to the [FFLogs API portal](https://www.fflogs.com/api/clients/).
2. Click **Create Client**.
3. Enter a client name (e.g. `PassportCheckerReborn`).
4. Provide any Redirect URL (e.g. `https://example.com/`).
5. Leave **Public Client** unchecked.
6. Copy the generated Client ID and Client Secret into the plugin settings.
</details>

### Tomestone Integration Tab
Enter your Tomestone API key (Bearer token) and click **Save**.

<details>
<summary>How to obtain a Tomestone API key</summary>

1. Go to your [Tomestone Account Settings](https://tomestone.gg/profile/account).
2. Scroll to the **API access token** section.
3. Click **Generate access token**.
4. Copy the token into the plugin settings.
</details>

## Building from Source

Requires the [Dalamud .NET SDK](https://github.com/goatcorp/Dalamud) (v14+) and .NET 10.

```bash
dotnet restore
dotnet build
```

## License

This project is licensed under the [GNU Affero General Public License v3.0](LICENSE.md).
