# Codex webview bundle

This directory contains the frozen official Codex webview bundle used by the
CyberVinci/Theia port at:

`C:\Users\Rodrigo\Desktop\CyberVinci\Modificacoes\Codex\packages\codex\resources`

The synchronized host target identifies the upstream UI as
`OpenAI.chatgpt` `26.5527.31454`. The Visual Studio host injects its own
WebView2 transport, IDE theme variables, locale, and app-server bridge at
runtime; the bundled UI assets are otherwise kept unchanged.

The host's hash-guarded import map creates runtime compatibility copies for
reasoning effort controls and conversation forks.
The guarded adapters load the app-server catalog and saved model/effort
configuration initially, retaining them until explicit changes invalidate their
queries. Neither query polls or refetches on window focus or network reconnect;
the native catalog comes from the configured Codex CLI.
The host restarts an idle app-server when that CLI package changes. When provider
capacity overrides require a generated catalog, it also watches the shared CLI
cache's model content and rebuilds from the same snapshot used to identify that
process. Active work and incomplete cache writes defer this refresh.
The private VSAI home refreshes only a complete, newer native model cache from
the current user's shared Codex home. Native catalog changes also restart an idle
app-server and invalidate the model query. The official account card's model
refresh button requests this path explicitly; it does not change credentials,
configuration, or conversation storage.
`CodexConversationForkWebViewCompatibility` uses the native inclusive
`thread/fork.lastTurnId` cutoff because paginated
threads reject the frozen UI's fork-then-rollback flow. It retains the upstream
history hydration and does not rewrite the frozen assets or the source thread.

`codex-visual-studio-history-guard.js` is owned by this Visual Studio host and
is intentionally not copied from the CyberVinci source. It adds bounded
history controls and browser-native lazy rendering without changing the
frozen official bundle or its synchronized shim.

`codex-visual-studio-diagnostics.js` is also host-owned. It contributes the
opt-in diagnostics switch to the official settings surface while preserving
the frozen upstream bundle. Detailed browser logging remains disabled until
that setting is explicitly enabled.

Run `scripts/Sync-CyberVinciCodexWebview.ps1` to refresh this frozen copy from
the local CyberVinci source.

`vsai-providers.js` is host-owned. Its model editor exchanges `providers-state`,
`providers-save`, and `providers-discover` / `providers-discovery` messages with
the C# bridge. Discovery uses the configured service's `models` endpoint, or the
authenticated root `/api/models` endpoint for MIDAS. Keys stay in the protected
provider settings and are never returned in editor state. A discovery preview
is bound to its request and connection settings before it can be saved.

Provider catalogs keep remote snapshots, manual model IDs, per-field overrides,
and hidden-model preferences separately. Service discovery runs only from the
editor's Refresh button, and its preview is applied when the user saves.
There is no timer, focus, startup, or ordinary-request discovery trigger.
The persisted AutoSync field is retained for settings compatibility but does not
schedule work. Saves still require an idle host, merge under the settings
store's cross-instance lock, and invalidate the model/config queries.

The current native CLI has a process-wide model catalog. Discovered runtime
metadata is added only with a valid native baseline, a known context window,
and an unambiguous model ID. Official and conflicting provider entries remain
unchanged; the editor displays the corresponding runtime limitation. Maximum
output tokens are descriptive metadata, not a separately enforced CLI limit.
The host does not synthesize selectable reasoning levels from a boolean flag.
