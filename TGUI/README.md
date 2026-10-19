# DeepLagoon TGUI

This port uses the existing `Robust.Client.WebView` engine module. No RobustToolbox
source changes are required. Local packaged UI and remote wiki documents have
separate request policies and separate controls. A wiki page never receives the
TGUI bridge.

## Player preference and native fallback

The native first-entry dialog offers TGUI or SS14 classic and confirms that the
choice can be changed under Options → General → Interface style. The archived,
client-only `ui.interface_style` setting is `0` until an explicit choice, `1` for
TGUI, and `2` for classic. Unknown values use classic and do not dismiss onboarding.
The setting is local to the client installation. Existing client configurations
are asked once after updating; no database migration is required.

Each supported window has a native **SS14 / TGUI** button (in its title bar, or beside
embedded UI). The archived `ui.window_styles` contains explicit choices by stable
interface name. Unlisted windows inherit the global style; switching a window does
not alter that default or the choices of other interface types. Reopening a window
or restarting the client retains its choice. Multiple instances of the same type
use that saved default on their next open. Clear `ui.window_styles` to reset overrides.

Typed BUIs bind both presentations through `TguiPanel.BindStyle`; they replace only
the client window, preserving the server subscription and replaying its current state.
Embedded typed hosts pair `SetNativeFallback` with `SetTguiRestore`. Generic native
views switch through the shared panel. Editors use `useInterfaceStyleGuard` to block
switching while a local draft or upload is pending; native editors also check their
unsaved state before returning to TGUI. Browser failure does not archive a choice.

Every production TGUI must have a native counterpart. Upstream windows register
`TguiPanel.SetNativeFallback` and check `WindowInterfaceStyle.UseTgui(interfaceName)` before opening their
browser variant. New content interfaces implement a native view in
`NativeInterfacePanel`. `InterfaceStyleTest.EveryProductionTguiHasANativeView`
checks the production interface inventory. Browser creation is lazy; native-only
sessions do not create hidden TGUI browsers. Startup exceptions and exhausted
health-check recovery use the same fallback, without changing the saved choice.

Ship consoles share `components/ShipConsoleFrame.jsx` and `ShipConsoleSteel.scss`;
civilian terminals share `CivilianConsoleFrame.jsx/.scss`. The keyboard overlay
uses the shell's own nine-slice coordinates. Enable `TguiPanel.ConsoleKeyboardFeedback`
for a window using either shell: native input (including the radar above CEF)
then sends decorative `console/key` messages. This path never captures keys or
issues gameplay actions. DOM clicks/keys also animate the caps; release, blur,
hidden windows and unmount clear them. `ShipRadarConsole` kind `handheld`, selected
for item-based radar consoles, displays only the measured native radar aperture.
The ship hardware atlas is shared between browser SVG regions and
`ShuttleRadarGlassControl`, with fixed proportions outside the radar circle.

Run `node tools/shuttle-console-browser.test.mjs` (`PLAYWRIGHT_PATH`) and
`node tools/civilian-console-browser.test.cjs` (`PLAYWRIGHT_MODULE`) after building
TGUI. These check geometry, controls and visible key travel; the latter uses the
shipped production bundle. Native routing/fallback tests are
`ShuttleCombatConsoleTests` and `CivilianConsoleTests`. Actual-client visual and
input checks remain necessary before release.

After C# console-host changes, also run `dotnet test Content.Tests/Content.Tests.csproj
--filter FullyQualifiedName~ClientModulesPassEngineTypeChecks`. This verifies both
client and shared assemblies with the engine's sandbox and IL verifier; compilation
and the UI integration fixtures alone do not establish that the modules can load.

Native views cover interaction actions/custom actions/body visibility, character
cards and image galleries, save prompts, apartment travel and furnishing, grid
editing, art and tattoos. Complex editors expose simpler native controls and keep
changes local until Apply/Save, with draft reset and close guards. The native
character editor also exposes body customization, tattoos and profile images.
Browser-only drafts that have not reached the host cannot be recovered after a
renderer failure; fallback replays the latest authoritative host state. Reopen
an existing native window after switching to TGUI.

Before release, verify first entry, reconnect after each choice, changing the
setting, a failed browser load, and native editor apply/reset in the actual
official-launcher client. Headless tests do not establish visual or in-game parity.

Vending machines use the shared `VendingMachine` catalogue interface: a brushed-metal
cabinet, dark product shelves, a large layered sprite preview, hover descriptions and
a red dispense button matching the shuttle console. A product click selects its preview; only
the dispense button sends the existing `VendingMachineEjectMessage`. Inventory type
is preserved when the same prototype appears in multiple inventories. The host
checks the current allowed catalogue for actions and icon requests; the server
retains authority over access, stock, pricing and payment. First-frame icons load
only near the visible shelves, without spawning preview entities. A failed page or
an eight-second readiness timeout opens the native vending menu instead.

Product cells have recessed sides and floors with first-frame sprite relief and
contact shadows. Products stand upright without tilt or rotation. The selected
settings are 7 px depth, 0.75 warm lighting and 0.6 unlit brightness. Selection and
cell lighting change only on button activation; hover shows a tooltip and keyboard
focus shows an outline. Search preserves the clicked selection without selecting
another product automatically. No product is selected when the window first opens.
The cabinet has folded metal edges, hinges and screws. Scratches, paint chips and
dents are seeded once per window and placed only in exposed metal areas; text,
controls and the product/payment panels are excluded. Resizing recomputes the
layout with the same seed. No idle animation regenerates the wear.
The upper panel is black inset glass with a full-width static lamp-matrix brand
display. Local Latin/Cyrillic glyphs avoid font downloads. Generic vending-machine
phrases and the dispenser subtitle are omitted from the sign.

Run `node tools/vending-machine-browser.test.cjs` after a production TGUI build
(`PLAYWRIGHT_MODULE` may point to the workspace Playwright package). The focused
`VendingMachineTguiTests` integration fixture checks the full vending icon catalogue,
free prices, inventory identities and unlimited stock. In-game checks should cover
paid/free machines, cash and Ironman characters, access-controlled machines,
emag/contraband changes, restocking, loss of power and repeated close/reopen cycles.

Embedded windows inherit the player's chat colors and light/dark theme. The
character editor also publishes its own background preset to the native layer;
other interfaces draw no animation unless they explicitly configure it.
`TguiPanel` polls appearance only while visible and
deserializes the saved chat settings only when their source string changes.

Right-clicking a character preview equipment slot records an explicitly empty
slot in that job's saved role loadout. Mandatory/hidden loadout groups and job
starting gear respect this choice on preview rebuild and server spawn. Selecting
replacement clothing restores its slot. `UnequippedLoadoutSlots` adds a nullable
database column for these choices; deploy the rebuilt client and server together.

In the game, chat background animation is drawn by `NativeChatBackground` under
the transparent CEF chat. All eleven presets and their intensity setting remain
available as native shader equivalents; reduced-motion freezes the effect.
The document publishes appearance/viewport changes through `native-background`,
but background movement itself generates no DOM updates or CEF texture uploads.
Message animations, scrolling and input can still repaint the browser.
The standalone browser preview retains the original CSS backgrounds until the
native host advertises support. Hidden/suspended/reloaded views reset the native
layer, and each ready notification replays the current appearance even when it
has not changed. This feature uses content code and a content shader, without an engine
source adapter.

For a visual smoke test, run `node TGUI/tools/native-background-preview.cjs` from
the repository root and open `http://127.0.0.1:8176` (or `/?editor` for the character
editor). It composites the actual
packaged chat over the same shader in WebGL and displays background bridge calls.
After settings settle, the count should stay constant while the effect moves.
This is a browser composition check, not an in-game CEF performance measurement.

`MSBuild/DeepLagoon.Network.targets` also compiles adapted copies of the engine's
network sources in `obj`, without editing RobustToolbox. It makes connection
status waiter removal atomic and completion tolerant of cancellation. This fixes
the IPv6/IPv4 losing-attempt race triggered by `localhost` when the main thread
stalls past the Happy Eyeballs delay; both address families remain available.
Already-cancelled tokens cannot leave a stale waiter. The build fails if the
expected engine source anchors change. Normal builds apply the adapter; use the
resulting `Robust.Shared.dll` along with the rebuilt client. Reusing a prebuilt
engine DLL bypasses this adapter.

Window resizing and automatic UI scaling keep the same browser document alive.
`MSBuild/DeepLagoon.WebView.targets`, imported by the root `Directory.Build.targets`,
adapts the WebView build to notify CEF of scale changes and reuse unchanged-size
textures. It reads two engine sources, checks that each expected source fragment
occurs exactly once, and compiles adapted copies in the WebView project's `obj`
directory instead of the original files. RobustToolbox sources and its pinned
commit stay unchanged. An incompatible engine update fails the build with an
adapter diagnostic; review the adapter before updating its source fragments.
Normal client builds automatically apply it; no separate command is needed.

Chat uses stock WebView mouse delivery: native keyboard focus is acquired for
the click, then the DOM bridge releases it after mouseup unless an editable field
is selected. This avoids requiring a custom WebView engine module for clicks.
`node --test tools/chat-focus.test.cjs` checks background clicks, editable fields,
and returning from gameplay to an already-selected input.

Packaged `.rsic` previews use the loaded RSI's frame regions. The minimum X/Y of
its first south frames identifies the shared GPU atlas offset, which is subtracted
before cropping the original packed PNG. This is content-only code using existing
engine APIs; neither PNG metadata sandbox exceptions nor custom engine types are
required. `TguiPackedSpriteTest` uses the real RSI loader with a nonzero GPU atlas
offset and checks directional/animated states and row transitions.
Set `SS14_LAUNCHER_ENGINE_DIR` to an installed launcher's engine DLL directory when
running `ClientSandboxTest`: it prioritizes those DLLs over local engine builds
to check IL compatibility with the engine that players actually use.

`GameWebView` dispatches browser events through the UI manager's deferred-action
queue after frame traversal. Action and ready handlers can open or close windows
without modifying the collection currently being enumerated. Pending messages
are discarded when their browser is removed or disposed.

Use in-document TGUI `Dropdown` controls and RGB/HEX color controls. Native
HTML `<select>` and `<input type="color">` open CEF popup surfaces which this
WebView implementation does not composite separately; they can corrupt the
main view's paint buffer. Do not use native browser popup widgets in interfaces.

`TguiPanel` automatically moves its enclosing native window when that window is
directly attached to `WindowRoot`. Hold the left mouse button anywhere in the
browser and move it at least four UI pixels to drag; a stationary click works
normally. Dragging does not resize or reload CEF, and the release click is
suppressed. Embedded panels (chat, lobby and character editor) stay anchored.
No per-interface drag header, JavaScript action or registration is needed.

## Create an interface

Quick start: `pnpm new:interface MyInterface` generates a TSX interface, shared
component, server system with validated counter actions, and a working console
prototype (`ComputerMyInterface`). It checks all destinations and refuses to
overwrite existing files. Replace the example state/actions with the feature's
own logic. The transport, request policy and native window are shared.

1. Add `packages/tgui/interfaces/MyInterface.tsx`. Export a component named
   `MyInterface`; see `DeepLagoonDemo.tsx` for `useBackend`, `data`, and `act`.
2. Build any DeepLagoon content project or the solution; TGUI is rebuilt automatically.
   Commit the changed source and lockfile. Generated bundles in
   `Resources/Web/DeepLagoon` are ignored by Git.
   Clients do not need Node.js. The bundles are distributed as game resources.
3. Register `WebUiBoundUserInterface` under a UI key on the entity's
   `UserInterface` component, and open it through the normal `UserInterfaceSystem`.
4. Publish `new WebUiState("MyInterface", jsonObject)` with `SetUiState`.
   Subscribe to `WebUiActionMessage` on the relevant server component and validate
   each supported action and its arguments. Never trust values supplied by JS.

The example entity is `ComputerTguiDemo`. Spawn it in a development session and
interact with it. Increment/reset are validated and applied by `WebUiDemoSystem`;
its state is shared by viewers through the normal bound UI transport.

Interfaces are automatically discovered by name; TSX/JSX files need no central
registry or separate client window class. For client-owned screens, instantiate
`TguiPanel`, call `SetState("MyInterface", jsonObject, title)`, and subscribe to
`OnAction`. `TguiActionData.TryParse` provides sandbox-compatible scalar argument
parsing; the consumer still validates field ranges and permissions. Use distinct
UI keys when one entity has multiple independent bound interfaces.

## Automatic builds

Every normal build of a DeepLagoon content project (including server, client,
packaging and tools) rebuilds the interface and chat production bundles. The
shared `DeepLagoon.TGUI.csproj` dependency prevents concurrent bundle writes
within a parallel solution build. Debug, DebugOpt, Release, Rebuild and Publish
use the same production bundles. IDE design-time checks do not run pnpm.

Install Node.js 22.13+ and pnpm 11.25.0 on build machines.

On Windows, builds also append an existing Codex bundled runtime to their child
process PATH when it is available. This lets VS Code build with the same tools
without changing the user's system PATH; installed commands still take priority.
`TguiRuntimeDirectory` can override the fallback dependency directory.

JavaScript dependencies are installed with `pnpm install --frozen-lockfile` on the first build and when
`package.json`, `pnpm-lock.yaml` or `pnpm-workspace.yaml` changes. The workspace
configuration permits install scripts only for the pinned esbuild and
@parcel/watcher versions; no interactive `pnpm approve-builds` step is needed.
Any install or bundle error fails the
normal build. To use a pnpm executable outside PATH, pass
`-p:TguiPackageManager="/path/to/pnpm"` (quote the executable path inside the
property value if it contains spaces). `pnpm build` remains available for a
standalone web build. `--no-build` commands reuse existing bundles.

## Dev Server and hot reload

From `TGUI` with Node.js 22.13+ and pnpm 11.25.0 installed:

```text
pnpm install --frozen-lockfile
pnpm dev
```

Start the Debug client with `--cvar tgui.dev_server=http://127.0.0.1:5173`, or run
`cvar tgui.dev_server http://127.0.0.1:5173` in its console before opening a TGUI
window. Then open `tgui_preview MyInterface` or the actual entity-bound UI.
Chat uses the same development server. TSX/JSX and SCSS changes update the already
open window through Vite HMR/React Fast Refresh. Compatible component edits retain
React local state and the current backend data. Refresh-boundary/export changes
may reload the document; the native window remains open and the host replays its
latest state after `ready`.

Use port 5173 for the supplied configuration/CSP. The server listens only on
127.0.0.1. The opt-in CVar is client-only, not saved, and ignored in Release.
Only a validated loopback HTTP origin is admitted; wiki access remains confined
to the guidebook. Stop the server and restart without the CVar to use packaged
bundles. Production builds never require a development server.

Action payloads are limited to 8 KiB, with a frontend budget of 7900 characters.
Oversized BYOND messages, shell commands, BYOND hotkeys, external links and
embedded remote pages are not implemented. Native windows own resizing and
closing. New interfaces can use TGUI components, layouts and local state hooks.
BYOND-specific features such as `ByondUi` require an SS14-specific replacement.

`tgui_preview DeepLagoonDemo`, `tgui_preview chat`, and `tgui_preview wiki` open
developer previews from the client console. Preview actions do not change server
state. Use `ComputerTguiDemo` to test actual client/server actions.

## TGUI chat

Settings → Misc → Classic chat (Классический чат) restores the original native `OutputPanel`
and horizontal input in lobby and round, with no chat CEF browser. During rounds
it selects the original floating chat HUD; disabling the option restores the
previous HUD layout. The archived client-only setting is `ui.vanilla_chat`.
The shared message history and input draft survive switching. Character TGUI
and wiki windows are independent of this chat setting.

The separated chat layout uses TGUI's renderer, tabs, search, highlights and
appearance settings. Enable the separated chat layout in game settings. SS14's
TGUI mode hides the native bottom input. Chat focus keybindings and the pen button
open the `ChatComposer` interface; Enter submits, Shift+Enter inserts a newline,
and Escape closes while retaining the draft. The mounted form confirms focus
to the host, which focuses the native browser and starts platform text input.
While that browser owns keyboard focus, Return is bridged to a DOM keydown;
this avoids the engine CEF adapter's extra Backspace char event. IME confirmation
is passed through normally, and the keyboard hook is removed on close.
Emotes expand the form from 440px
to 840px and the input from 66px to 250px over 180ms, inside a fixed browser
surface. The browser is disposed when the composer closes. Draft updates are
debounced; state replay supports dev-server reload without losing the draft.
Channel selection, typing indicators and server permissions remain connected
through the existing `ChatUIController`.

Only messages received by the client enter the web panel. SS14 markup is converted
to escaped HTML with supported rich-text styles (bold, italic, color and relative
font size). Channel metadata selects TGUI message classes; text prefixes never
determine the channel. Explicit server colors, including departmental radio colors,
are preserved. The renderer uses HTML for display and plain text for searching,
and never combines different message types or formatting just because text matches.
The light/dark message styles remain identical to the TGUI source files.
Message history is never restored from
browser storage; server deletions rebuild the panel from current client history.
Settings are stored in the existing chat preference record, in the YAML
`WebState` field, alongside native chat settings. Pinned emote IDs use the
`PinnedEmotes` field in the same record. Click the star above chat to search and
pin/unpin emotes (up to 24); pinned buttons execute the regular predictive SS14
event and follow species, whitelist and availability restrictions. The visible
12-pixel grip between viewport and chat adjusts its width (15–55%); the proportion
is saved in `ui.chat_panel_width`. The imported descriptive emotes are defined in
`Resources/Prototypes/_DeepLagoon/Voice/extended_emotes.yml`.

BYOND's audio player, map controls and arbitrary external chat images
are not included in this port. TGUI fonts and icons are packaged locally.

## Wiki guidebook

Edit `Resources/Prototypes/_DeepLagoon/WebUI/wiki.yml`. Each `wikiPage` has an ID,
localized name, explicit `/ru/...` path, optional `default: true`, and the old
`guideEntries` IDs it replaces. Initially the only allowed page is `/ru/rules`.
Unmapped contextual help displays a message instead of loading an unrelated page.

The guidebook button, keybinding, help API and new-player automatic opening use
the wiki window. Add more pages and mappings as the wiki grows. Only HTTPS on
`wiki.deep-lagoon-ss14.ru`, configured paths without query strings, and local
wiki assets are allowed. Other pages, origins, downloads, login/API requests and
external resources are blocked. In-page anchors are permitted.

Server code can show a configured page to one player:

```csharp
EntityManager.System<Content.Server._DeepLagoon.WebUI.WikiPageSystem>()
    .OpenPage(session, "Rules");
```

This sends a page ID, not a URL, and always opens the guidebook window. Local
TGUI windows and the chat reject every wiki/network request.

## Build

Build the normal client and server projects with .NET 10. CEF's subprocess,
native libraries and resources are required, not just the managed WebView DLL.
The client project references the existing WebView module and copies the
subprocess apphost for local builds. The content manifest declares the module;
the launcher's existing module downloader installs its matching engine release.
Confirm that the chosen engine release publishes that module before deployment.

```text
dotnet build Content.Client/Content.Client.csproj -c Debug
dotnet build Content.Server/Content.Server.csproj -c Debug
```

## Character editor and emote audio

The character editor uses one `CharacterEditor.jsx` browser for every tab,
identity, profile selection and save/import/export controls. Only the transparent
character sprite is drawn by the engine above the TGUI background. The editor
inherits the player's chat theme and background animation. Collapsible hair and
beard galleries show sprite previews; color squares open the color wheel.
Equipment customization uses modal dialogs and the existing per-profile data,
server validation and instance visuals. Paint dialogs also expose RGB and HSV.
Web actions modify the same `HumanoidCharacterProfile` draft; saving uses the
existing controller callbacks, species restrictions, prices and trait budgets.

In a connected lobby, `tgui_preview character` opens the actual editor with a local test draft;
it does not save an account profile. TGUI Dev Server/Fast Refresh works through
the same `TguiPanel` used by other interfaces.

The web panel has a permanent parent above the native tab content; tab changes
update its data instead of closing/recreating CEF. Its viewport does not contribute
tab-specific minimum widths to the lobby layout. Hidden editors release their
browser, and reopening/reloading replays the latest cached draft on `ready`.
Identical states are not resent; chat history replacement uses `chat/replace`
inside the existing document instead of reloading the lobby chat page.
Both the lobby and the separated in-round screen use the same `ChatBox`
TGUI integration, native input and saved web settings. Hidden chat views release
CEF and restore settings/history on reopening. When TGUI is enabled, incoming
messages retain native audio/read handling without building a duplicate hidden
rich-text history. Performance validation must measure an actual round;
editor/menu lifecycle checks do not establish in-round FPS.
Server/character details scroll on shorter lobby windows so the chat viewport
retains a usable minimum height.
Emote audio files and sex-dependent/random bindings are copied from TGUI;
regenerate with
`node tools/port-emote-sounds.cjs <source repository path>` (also invoked by
the emote importer). `SOURCE.txt` records hashes; upstream voice attribution
is preserved beside the imported files. The picker supports a responsive grid,
an explicit Close button, Escape, auto-close after performing an emote and
stars for pinning without closing the list.

## Upstream source

`packages/common`, `packages/tgui`, `packages/tgui-panel`, and
`packages/tgui-dev-server` were copied from the user's MOLOT-BlueMoon-Station
checkout at `a3c313de9c32aa8ed651a8009dbb389e9a15f438`. Unrelated game interfaces and upstream tests were excluded. Original
copyright/license headers are preserved; see `UPSTREAM-LICENSE` for the source
repository license. Local modifications adapt the transport, settings and chat
history. Font Awesome includes its original headers and README in game resources.
