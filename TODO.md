# Passport Checker Reborn – TODO

> Tracked features and their current implementation status.
> ✅ = working code, ⚠️ = partially implemented, 🔲 = not yet implemented.

---

## Working Features

### ✅ PF Detail Overlay
Member info overlay that auto-opens/closes alongside `LookingForGroupDetail`.
Positioning (left/right) reads the addon's `AtkUnitBase*` and anchors accordingly.
**Files:** `Windows/OverlayWindow.cs`, `Services/PartyFinderManager.cs`

### ✅ Party Job Icons
Renders in-game job icons via `TextureProvider.GetFromGameIcon()` with a text
abbreviation fallback.
**Files:** `Windows/OverlayWindow.cs`

### ✅ FFLogs Integration
OAuth 2.0 client-credentials flow, GraphQL v2 queries, per-job parse percentiles
with colour coding. 10-minute TTL cache. Data fetched on button click only.
**Files:** `Services/FFLogsService.cs`, `Windows/OverlayWindow.cs`

### ✅ Tomestone.gg Integration
Three-endpoint API (progression-graph, activity, profile) with Bearer token auth.
Shows prog point, clears, and best parse. Lodestone ID fallback for browser links.
**Files:** `Services/TomestoneService.cs`, `Windows/OverlayWindow.cs`

### ✅ High-End Duty Detection
Checks `ContentFinderCondition.HighEndDuty` flag and name keywords (Savage,
Ultimate, Extreme, Criterion, Unreal, Chaotic). Gate for overlay-only-on-high-end setting.
**Files:** `Services/PartyFinderManager.cs`

### ✅ Auto-Refresh PF Listings
Timer-based refresh at a configurable interval (10–120 s). Pauses while the
detail pane is open. Dispatches via `IFramework.RunOnFrameworkThread`.
**Files:** `Services/PartyFinderManager.cs`

### ✅ Party List Overlay
Shows FFLogs/Tomestone data for current party members. Reads `IPartyList` +
`InfoProxyCrossRealm` fallback for cross-world parties. Configurable position
(Left / Right / Above / Below / Unbound). Auto-hide in duty/combat.
**Files:** `Windows/PartyListWindow.cs`, `PassportCheckerReborn.cs`

### ✅ PF Member Name Discovery
`IAddonLifecycle` hooks + `AgentLookingForGroup.PopulateListingData` hook to
intercept `MemberContentIds` and `Jobs` arrays. CharaCard adventure-plate
lookup for name resolution.
**Files:** `Services/PartyFinderManager.cs`

---

## Partially Implemented

### ⚠️ Known Player Border
Rendering (`AddRect()`) and colour config work. Data source for the known-player
list is a stub — needs Friends List integration or a local cache.
**Files:** `Services/PartyFinderManager.cs`, `Windows/OverlayWindow.cs`

### ⚠️ Right-Click → View Recruitment
Context-menu entry is registered. Click handler currently logs + prints a chat
message. Needs to open PF filtered to the target player's listing.
**Files:** `Services/PartyFinderManager.cs`

---

## Not Yet Implemented

> Settings for these features are disabled in the config UI with
> `ImGui.BeginDisabled` / `ImGui.EndDisabled`.

### 🔲 True Time-Based Sorting
Re-sort PF listings by creation timestamp. Hook registered but handler is a
no-op. Requires reading timestamps from AtkComponentList or cached packet data.
**Files:** `Services/PartyFinderManager.cs`

### 🔲 Expand Listings to 100 Per Page
Increase visible PF entries. Hook registered but handler is empty. Requires
patching `AgentLookingForGroup` or manipulating AtkComponentList item count.
**Files:** `Services/PartyFinderManager.cs`

### 🔲 One-Click Job Filter
Inject a button into `LookingForGroup` that applies a job filter for high-end
duties. Requires dynamic AtkComponentButton node injection.
**Files:** `Services/PartyFinderManager.cs`

### 🔲 Prevent PF Auto-Close on Party Changes
Stop the game from closing the PF window when party composition changes. Needs
signature scanning + `Hook<T>` detour; signature is patch-dependent.
**Files:** `Services/PartyFinderManager.cs`
