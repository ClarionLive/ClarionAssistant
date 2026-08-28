; Clarion Assistant v5.3 Installer
; Inno Setup 6 Script
; Supports Clarion 10, 11, 12 — user picks which version(s) to install

#define MyAppName "Clarion Assistant"

; MyAppVersion is NOT defined here. It is supplied on the ISCC command line by
; installer\build-installer.ps1, which reads <FullVersion> from ClarionAssistant\Version.props and
; passes /DMyAppVersion=<value>.
;
; It used to be a hardcoded #define, and the comment that sat here admitted it was "a MANUAL step
; at every release cut" that was "silent" when left stale - which is exactly what happened: the
; file said 5.8.1 while Version.props said 5.8.1165. Nothing caught it, because the freshness gate
; compares per-config BINARY stamps and never looks at this file. The symptoms were an installer
; named for the wrong version and an Add/Remove Programs entry disagreeing with every DLL it had
; just installed.
;
; Erroring out is deliberate. A default would restore the silent-drift failure in a new costume:
; the build would succeed and ship the wrong number. Compiling this script by hand (rather than
; through build-installer.ps1) is now an explicit choice you have to make:
;     ISCC.exe /DMyAppVersion=5.8.2 ClarionAssistant.iss
#ifndef MyAppVersion
  #error MyAppVersion not supplied. Build via installer\build-installer.ps1, which passes it from Version.props.
#endif
#define MyAppPublisher "ClarionLive"
#define MyAppURL "https://clarionlive.com"

; Source directories
; SrcBase/SrcDocs/SrcInstaller resolve relative to THIS script's own location ({#SourcePath}),
; so compiling works regardless of which machine or drive the repo is checked out to.
; SrcAgents/SrcBlankDct resolve via GetEnv to whichever account runs ISCC, not one developer's
; profile. The remaining Src* vars point at repos/installs that live OUTSIDE this repo
; (ComForClarion, UltimateCOM, ClarionCOM tooling, a Clarion 12 install, Node.js) — override via
; the env vars noted below if you build those elsewhere. Their [Files] entries are guarded by the
; Have* presence flags defined at the end of this section, so compiling without them skips those
; optional pieces — LOUDLY, via one #pragma warning each — instead of failing outright.
; NOTE: ISPP's #ifexist / FileExists match FILES ONLY (a directory yields FALSE even when it
; exists), so every flag probes a sentinel FILE inside its source; only wildcard-only trees
; with no stable filename use DirExists.
#define SrcBase SourcePath + "..\ClarionAssistant"
#define SrcC10 SrcBase + "\bin\Debug-C10"
#define SrcC11 SrcBase + "\bin\Debug-C11"
; 11 and 11.1 are distinct Clarion releases with separate binding DLLs (see deploy.ps1) — never
; share a build output between them.
#define SrcC11_1 SrcBase + "\bin\Debug-C11.1"
#define SrcC12 SrcBase + "\bin\Debug-C12"
; Indexer is VENDORED into the repo (GitHub #30) — source from ClarionAssistant\indexer, not the old external H:\DevLaptop\ClarionLSP tree.
#define SrcClarionIndexer SrcBase + "\indexer\bin\Debug"
; ClarionCOMBrowser (COM for Clarion IDE addin) lives in a separate repo. Override: CLARIONCOMBROWSER_DIR
#define SrcComForClarion GetEnv("CLARIONCOMBROWSER_DIR") != "" ? GetEnv("CLARIONCOMBROWSER_DIR") : "H:\DevLaptop\ClarionIdeCOMPane\ClarionCOMBrowser\bin\Debug"
; Plugin marketplace is no longer bundled by the installer — configure.ps1
; installs it from the GitHub repo ClarionLive/clarionassistant-marketplace.
; Repo source of truth: marketplace\ (publish via publish-marketplace-to-github.ps1).
#define SrcAgents GetEnv("USERPROFILE") + "\.claude\agents"
#define SrcBlankDct GetEnv("APPDATA") + "\clarionassistant"
#define SrcDocs SourcePath + "..\docs"
#define SrcTerminal SrcBase + "\Terminal"
#define SrcTaskBoard SrcBase + "\TaskLifecycleBoard"
; UltimateCOM class/template sources live outside this repo. Override: ULTIMATECOM_CLASSES_DIR / ULTIMATECOM_TEMPLATES_DIR
#define SrcUltimateClasses GetEnv("ULTIMATECOM_CLASSES_DIR") != "" ? GetEnv("ULTIMATECOM_CLASSES_DIR") : "H:\Dev\Source\Classes"
#define SrcUltimateTemplates GetEnv("ULTIMATECOM_TEMPLATES_DIR") != "" ? GetEnv("ULTIMATECOM_TEMPLATES_DIR") : "H:\Dev\Source\SharedTemplates"
; Template DLLs / COM docs ship from a Clarion 12 install. Override: CLARION12_ROOT
#define SrcTemplateDlls (GetEnv("CLARION12_ROOT") != "" ? GetEnv("CLARION12_ROOT") : "C:\Clarion12") + "\accessory\template\win"
#define SrcComDocs (GetEnv("CLARION12_ROOT") != "" ? GetEnv("CLARION12_ROOT") : "C:\Clarion12") + "\accessory\resources\ComForClarionDocumentation"
; ClarionCOM tooling scripts live outside this repo. Override: CLARIONCOM_TOOLING_DIR
#define SrcClarionCOM GetEnv("CLARIONCOM_TOOLING_DIR") != "" ? GetEnv("CLARIONCOM_TOOLING_DIR") : "H:\DevLaptop\ClarionCOM\COMTemplate"
#define SrcFts5 SrcBase + "\lib\sqlite-fts5"
; Bundled LSP is now PURE/stock upstream (GitHub #40) — source from the pinned pure build under
; .lsp-build\<tag>, NOT the old codegraph-overlay clone. Tag tracks lsp-server-sync\lsp-snapshot.json
; "resolvedTag"; bump this path when the pin bumps (Sync-LspServer.ps1 -Pure -Tag <tag>).
#define SrcLsp SrcBase + "\.lsp-build\v1.0.0"
; Bundled node.exe (so end users don't need Node.js installed). Override: CLARIONLSP_NODE
#define SrcNodeExe GetEnv("CLARIONLSP_NODE") != "" ? GetEnv("CLARIONLSP_NODE") : "C:\Program Files\nodejs\node.exe"
; Bundled Markdown Editor — msarson/ClarionMarkdownEditor, redistributed under MIT. Upstream ships a
; PREBUILT release zip, so unlike the LSP there is nothing to compile: Sync-MarkdownEditor.ps1 just
; downloads, verifies and extracts it. Tag tracks markdown-editor-sync\markdown-snapshot.json
; "resolvedTag"; bump BOTH defines below when the pin bumps (Sync-MarkdownEditor.ps1 -Tag <tag>).
#define SrcMarkdown SrcBase + "\.markdown-build\v1.3.0"
; The version the only-if-newer check compares against a user's existing install. MUST equal
; markdown-snapshot.json "resolvedIdentityVersion" — which is the <Identity version> from the .addin,
; NOT the DLL's FileVersion. Upstream freezes FileVersion at 1.0.2.0 across every release, so Inno's
; built-in newer-file comparison cannot tell v1.0.2 from v1.2.0 and must not be relied on here.
#define MarkdownPinVersion "1.3.0"
; The directory containing this .iss file itself (SourcePath already ends in "\").
#define SrcInstaller Copy(SourcePath, 1, Len(SourcePath)-1)
; Repo root — for THIRD-PARTY-NOTICES.md, which must ship wherever the addin does.
#define SrcRepoRoot SourcePath + ".."

; ---- Optional-source presence flags ----
; Each probes a sentinel FILE (never a bare directory — ISPP #ifexist/FileExists return FALSE
; for directories). A missing source drops its [Files] entries and emits exactly one warning
; below, so the packaging log always shows what was omitted from the installer.
#define HaveNodeExe FileExists(SrcNodeExe)
#define HaveLsp FileExists(SrcLsp + "\out\server\src\server.js")
; Two sentinels, deliberately: the .addin is the payload, and the LICENSE is the MIT notice we are
; obliged to redistribute with it. The sync stages the licence because upstream's zip omits it, so
; a payload present WITHOUT it means someone unzipped by hand and we must not ship that.
#define HaveMarkdown FileExists(SrcMarkdown + "\ClarionMarkdownEditor.addin")
#define HaveMarkdownLicense FileExists(SrcMarkdown + "\LICENSE-ClarionMarkdownEditor.txt")
#define HaveC11_1 FileExists(SrcC11_1 + "\ClarionAssistant.dll")
#define HaveComForClarion FileExists(SrcComForClarion + "\ClarionCOMBrowser.dll")
#define HaveUltimateClasses FileExists(SrcUltimateClasses + "\UltimateCOM.inc")
#define HaveUltimateTemplates FileExists(SrcUltimateTemplates + "\UltimateCOM.tpl")
#define HaveTemplateDlls FileExists(SrcTemplateDlls + "\UCSelectCOM.dll")
#define HaveComDocs DirExists(SrcComDocs)
#define HaveClarionCOM FileExists(SrcClarionCOM + "\version.txt")
#define HaveBlankDct FileExists(SrcBlankDct + "\blank.dct")
#define HaveAgents FileExists(SrcAgents + "\code-reviewer.md")

#if !HaveNodeExe
#pragma message "WARNING: node.exe missing (" + SrcNodeExe + ") - shipping WITHOUT the bundled Node runtime; the LSP server cannot start without it."
#endif
#if !HaveLsp
#pragma message "WARNING: bundled LSP build missing (" + SrcLsp + ") - shipping WITHOUT the Clarion LSP server. Run Sync-LspServer.ps1 -Pure first."
#endif
#if !HaveMarkdown
#pragma message "WARNING: bundled Markdown Editor missing (" + SrcMarkdown + ") - shipping WITHOUT the Markdown Editor addin. Run markdown-editor-sync\Sync-MarkdownEditor.ps1 first."
#endif
; Not a nice-to-have: MIT requires the notice to travel with the copy. Refuse to ship the payload
; silently stripped of it — this warning fails the build the same way a missing payload does.
#if HaveMarkdown && !HaveMarkdownLicense
#pragma message "WARNING: Markdown Editor LICENSE missing (" + SrcMarkdown + "\LICENSE-ClarionMarkdownEditor.txt) - shipping WITHOUT the MIT notice we are required to redistribute. Re-run Sync-MarkdownEditor.ps1 -Force."
#endif
#if !HaveC11_1
#pragma message "WARNING: bin\Debug-C11.1 missing - shipping WITHOUT the Clarion 11.1 addin (build it via deploy.ps1 -Version 11.1)."
#endif
#if !HaveComForClarion
#pragma message "WARNING: ClarionCOMBrowser build missing (" + SrcComForClarion + ") - shipping WITHOUT the COM for Clarion addin."
#endif
#if !HaveUltimateClasses
#pragma message "WARNING: UltimateCOM classes missing (" + SrcUltimateClasses + ") - shipping WITHOUT UltimateCOM.inc/.clw."
#endif
#if !HaveUltimateTemplates
#pragma message "WARNING: UltimateCOM templates missing (" + SrcUltimateTemplates + ") - shipping WITHOUT UltimateCOM.tpl."
#endif
#if !HaveTemplateDlls
#pragma message "WARNING: UltimateCOM template DLLs missing (" + SrcTemplateDlls + ") - shipping WITHOUT UCSelectCOM/UTFileCopy DLLs."
#endif
#if !HaveComDocs
#pragma message "WARNING: ComForClarion documentation missing (" + SrcComDocs + ") - shipping WITHOUT COM docs."
#endif
#if !HaveClarionCOM
#pragma message "WARNING: ClarionCOM tooling missing (" + SrcClarionCOM + ") - shipping WITHOUT ClarionCOM templates/scripts."
#endif
#if !HaveBlankDct
#pragma message "WARNING: blank.dct missing (" + SrcBlankDct + ") - shipping WITHOUT the blank dictionary + ClassModels."
#endif
#if !HaveAgents
#pragma message "WARNING: Claude agents missing (" + SrcAgents + ") - shipping WITHOUT the quality agents."
#endif

[Setup]
AppId={{B7E2F4A1-8C3D-4E5F-9A1B-2C3D4E5F6A7B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\ClarionAssistant
DefaultGroupName={#MyAppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=ClarionAssistant-{#MyAppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x86compatible
UsedUserAreasWarning=no
SetupIconFile={#SrcInstaller}\clarion-assistant.ico
UninstallDisplayIcon={app}\ClarionAssistant.dll
LicenseFile={#SrcInstaller}\LICENSE.txt
InfoBeforeFile={#SrcInstaller}\PREINSTALL.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Full installation"
Name: "compact"; Description: "Compact installation (addins only)"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

; ============================================================
; COMPONENTS — Clarion version selection
; ============================================================

[Components]
; Clarion version checkboxes (auto-checked based on paths entered on previous page)
Name: "clarion10"; Description: "Clarion 10 Addin"; Types: full custom
Name: "clarion11"; Description: "Clarion 11 Addin"; Types: full custom
Name: "clarion111"; Description: "Clarion 11.1 Addin"; Types: full custom
Name: "clarion12"; Description: "Clarion 12 Addin"; Types: full compact custom
; COM for Clarion
Name: "comforclarion"; Description: "COM for Clarion Browser Addin"; Types: full compact custom
Name: "comforclarion\addin"; Description: "IDE Addin (COM Browser)"; Types: full compact custom; Flags: fixed
Name: "comforclarion\templates"; Description: "UltimateCOM Templates and Class"; Types: full custom
Name: "comforclarion\docs"; Description: "COM for Clarion Documentation"; Types: full custom
Name: "comforclarion\tooling"; Description: "ClarionCOM Project Templates and Scripts"; Types: full custom
; Plugin and agents
; The plugin (skills, hooks, docs) is installed from the GitHub marketplace by
; configure.ps1 — not bundled here. The plugin\skills sub-component is retained
; because it also gates the blank dictionary / class-model templates below.
Name: "plugin"; Description: "Clarion Assistant Plugin (installed from GitHub marketplace)"; Types: full custom
Name: "plugin\skills"; Description: "Clarion Assistant templates (blank dictionary, class models)"; Types: full custom; Flags: fixed
Name: "agents"; Description: "Claude Code Quality Agents"; Types: full custom
Name: "lsp"; Description: "Clarion Language Server (LSP)"; Types: full custom
; Third-party addin (msarson/ClarionMarkdownEditor, MIT) installed into its OWN addin folder, NOT
; under ClarionAssistant\. Clarion scans every subfolder of accessory\addins, and two copies sharing
; the ClarionMarkdownEditor Identity fail IDE startup outright ("Identity name used by multiple
; addins") — so the canonical folder is the only safe destination. Guarded by ShouldInstallMarkdown:
; an existing install that is NEWER than our pin is left alone.
Name: "markdown"; Description: "Markdown Editor addin (by Mark Sarson)"; Types: full custom
Name: "docgraph"; Description: "Pre-loaded Documentation Database"; Types: full custom
Name: "docs"; Description: "User Guide"; Types: full custom

; ============================================================
; FILES
; ============================================================

[Files]
; --- Clarion 10 Addin ---
Source: "{#SrcC10}\ClarionAssistant.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcC10}\ClarionAssistant.pdb"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcC10}\ClarionAssistant.addin"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
; Hard <Reference> of ClarionAssistant.dll (copy-local). Omitting it broke type instantiation
; on clean installs ("Cannot create object: MonacoClarionEditorDisplayBinding", ticket 0abd79df).
Source: "{#SrcC10}\ClarionLsp.Contracts.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcC10}\Microsoft.Web.WebView2.Core.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcC10}\Microsoft.Web.WebView2.WinForms.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcC10}\Microsoft.Web.WebView2.Wpf.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcC10}\WebView2Loader.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcC10}\runtimes\win-x86\native\WebView2Loader.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\runtimes\win-x86\native"; Components: clarion10; Flags: ignoreversion
;   PdfPig - in-process PDF text extraction for DocGraph ingestion (#167). The UglyToad.*
;   wildcard covers the seven PdfPig assemblies; the five shims below are NOT inbox on .NET
;   Framework 4.8 and arrive via PdfPig package dependencies. Omitting any one fails at
;   RUNTIME on the first PDF import, not at build - so a missing entry here looks exactly
;   like the silent 'no documents found' bug it replaced. Mirrors the block in deploy.ps1.
Source: "{#SrcC10}\UglyToad.*.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcC10}\Microsoft.Bcl.HashCode.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcC10}\System.Buffers.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcC10}\System.Memory.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcC10}\System.Numerics.Vectors.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcC10}\System.Runtime.CompilerServices.Unsafe.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
; Everything SDK native DLL — used by EverythingService P/Invokes (4 MCP search tools).
; Harmless if the user has no Everything service running; the DLL is just the SDK shim.
Source: "{#SrcC10}\Everything32.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcFts5}\System.Data.SQLite.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcFts5}\SQLite.Interop.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcFts5}\SQLite.Interop.dll"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\x86"; Components: clarion10; Flags: ignoreversion
; Terminal/ web assets — recursive copy so new pages/scripts ship automatically and the hand-list
; can't drift (fixes clean-install "Cannot create object" + missing Monaco assets, ticket 0abd79df).
; Excludes the C# source (compiled into the DLL) and dev-only mockups/tests; ClassModels ships (runtime).
Source: "{#SrcTerminal}\*"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\Terminal"; Components: clarion10; Flags: ignoreversion recursesubdirs; Excludes: "*.cs,\mockups\*,\test\*"
Source: "{#SrcTaskBoard}\lifecycle-board.html"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\TaskLifecycleBoard"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcClarionIndexer}\clarion-indexer.exe"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcClarionIndexer}\clarion-indexer.pdb"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
; Third-party license notices — ships wherever the addin does; the obligation travels with the binaries.
Source: "{#SrcRepoRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10; Flags: ignoreversion
Source: "{#SrcDocs}\ClarionAssistant-Guide.html"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\docs"; Components: clarion10 and docs; Flags: ignoreversion
; --- Clarion 10 LSP Server ---
#if HaveNodeExe
Source: "{#SrcNodeExe}"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\lsp-server"; Components: clarion10 and lsp; Flags: ignoreversion
; Node's own license bundle (V8, OpenSSL, ICU, zlib and the rest), shipped beside the binary
; it covers. Stable DestName so a Node upgrade replaces it instead of leaving the old one behind.
Source: "{#SrcRepoRoot}\third-party\node-v24.13.0-LICENSE.txt"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\lsp-server"; DestName: "node-LICENSE.txt"; Components: clarion10 and lsp; Flags: ignoreversion
#endif
#if HaveLsp
Source: "{#SrcLsp}\out\server\*"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\lsp-server\out\server"; Components: clarion10 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\out\common\*"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\lsp-server\out\common"; Components: clarion10 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-jsonrpc\*"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-jsonrpc"; Components: clarion10 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-languageserver\*"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-languageserver"; Components: clarion10 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-languageserver-protocol\*"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-languageserver-protocol"; Components: clarion10 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-languageserver-textdocument\*"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-languageserver-textdocument"; Components: clarion10 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-languageserver-types\*"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-languageserver-types"; Components: clarion10 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\xml2js\*"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\xml2js"; Components: clarion10 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\sax\*"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\sax"; Components: clarion10 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\xmlbuilder\*"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\xmlbuilder"; Components: clarion10 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\iconv-lite\*"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\iconv-lite"; Components: clarion10 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\safer-buffer\*"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\safer-buffer"; Components: clarion10 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

; --- Clarion 11 Addin ---
Source: "{#SrcC11}\ClarionAssistant.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcC11}\ClarionAssistant.pdb"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcC11}\ClarionAssistant.addin"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
; Hard <Reference> of ClarionAssistant.dll (copy-local) — see C10 note. Ticket 0abd79df.
Source: "{#SrcC11}\ClarionLsp.Contracts.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcC11}\Microsoft.Web.WebView2.Core.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcC11}\Microsoft.Web.WebView2.WinForms.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcC11}\Microsoft.Web.WebView2.Wpf.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcC11}\WebView2Loader.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcC11}\runtimes\win-x86\native\WebView2Loader.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\runtimes\win-x86\native"; Components: clarion11; Flags: ignoreversion
;   PdfPig - in-process PDF text extraction for DocGraph ingestion (#167). The UglyToad.*
;   wildcard covers the seven PdfPig assemblies; the five shims below are NOT inbox on .NET
;   Framework 4.8 and arrive via PdfPig package dependencies. Omitting any one fails at
;   RUNTIME on the first PDF import, not at build - so a missing entry here looks exactly
;   like the silent 'no documents found' bug it replaced. Mirrors the block in deploy.ps1.
Source: "{#SrcC11}\UglyToad.*.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcC11}\Microsoft.Bcl.HashCode.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcC11}\System.Buffers.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcC11}\System.Memory.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcC11}\System.Numerics.Vectors.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcC11}\System.Runtime.CompilerServices.Unsafe.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcC11}\Everything32.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcFts5}\System.Data.SQLite.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcFts5}\SQLite.Interop.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcFts5}\SQLite.Interop.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\x86"; Components: clarion11; Flags: ignoreversion
; Terminal/ web assets — recursive copy (see C10 block above). Ticket 0abd79df.
Source: "{#SrcTerminal}\*"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\Terminal"; Components: clarion11; Flags: ignoreversion recursesubdirs; Excludes: "*.cs,\mockups\*,\test\*"
Source: "{#SrcTaskBoard}\lifecycle-board.html"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\TaskLifecycleBoard"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcClarionIndexer}\clarion-indexer.exe"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcClarionIndexer}\clarion-indexer.pdb"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
; Third-party license notices — ships wherever the addin does; the obligation travels with the binaries.
Source: "{#SrcRepoRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcDocs}\ClarionAssistant-Guide.html"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\docs"; Components: clarion11 and docs; Flags: ignoreversion
; --- Clarion 11 LSP Server ---
#if HaveNodeExe
Source: "{#SrcNodeExe}"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\lsp-server"; Components: clarion11 and lsp; Flags: ignoreversion
; Node's own license bundle (V8, OpenSSL, ICU, zlib and the rest), shipped beside the binary
; it covers. Stable DestName so a Node upgrade replaces it instead of leaving the old one behind.
Source: "{#SrcRepoRoot}\third-party\node-v24.13.0-LICENSE.txt"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\lsp-server"; DestName: "node-LICENSE.txt"; Components: clarion11 and lsp; Flags: ignoreversion
#endif
#if HaveLsp
Source: "{#SrcLsp}\out\server\*"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\lsp-server\out\server"; Components: clarion11 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\out\common\*"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\lsp-server\out\common"; Components: clarion11 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-jsonrpc\*"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-jsonrpc"; Components: clarion11 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-languageserver\*"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-languageserver"; Components: clarion11 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-languageserver-protocol\*"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-languageserver-protocol"; Components: clarion11 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-languageserver-textdocument\*"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-languageserver-textdocument"; Components: clarion11 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-languageserver-types\*"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-languageserver-types"; Components: clarion11 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\xml2js\*"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\xml2js"; Components: clarion11 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\sax\*"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\sax"; Components: clarion11 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\xmlbuilder\*"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\xmlbuilder"; Components: clarion11 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\iconv-lite\*"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\iconv-lite"; Components: clarion11 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\safer-buffer\*"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\safer-buffer"; Components: clarion11 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

; --- Clarion 11.1 Addin ---
; Whole block guarded: bin\Debug-C11.1 only exists once the 11.1 config has been built, and
; build-installer.ps1's freshness gate already treats a missing config bin as "won't ship this
; config" — without this guard ISCC would hard-fail instead. The HaveC11_1 warning above fires.
#if HaveC11_1
Source: "{#SrcC11_1}\ClarionAssistant.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcC11_1}\ClarionAssistant.pdb"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcC11_1}\ClarionAssistant.addin"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
; Hard <Reference> of ClarionAssistant.dll (copy-local) — see C10 note. Ticket 0abd79df.
Source: "{#SrcC11_1}\ClarionLsp.Contracts.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcC11_1}\Microsoft.Web.WebView2.Core.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcC11_1}\Microsoft.Web.WebView2.WinForms.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcC11_1}\Microsoft.Web.WebView2.Wpf.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcC11_1}\WebView2Loader.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcC11_1}\runtimes\win-x86\native\WebView2Loader.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\runtimes\win-x86\native"; Components: clarion111; Flags: ignoreversion
;   PdfPig - in-process PDF text extraction for DocGraph ingestion (#167). The UglyToad.*
;   wildcard covers the seven PdfPig assemblies; the five shims below are NOT inbox on .NET
;   Framework 4.8 and arrive via PdfPig package dependencies. Omitting any one fails at
;   RUNTIME on the first PDF import, not at build - so a missing entry here looks exactly
;   like the silent 'no documents found' bug it replaced. Mirrors the block in deploy.ps1.
Source: "{#SrcC11_1}\UglyToad.*.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcC11_1}\Microsoft.Bcl.HashCode.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcC11_1}\System.Buffers.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcC11_1}\System.Memory.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcC11_1}\System.Numerics.Vectors.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcC11_1}\System.Runtime.CompilerServices.Unsafe.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcC11_1}\Everything32.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcFts5}\System.Data.SQLite.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcFts5}\SQLite.Interop.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcFts5}\SQLite.Interop.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\x86"; Components: clarion111; Flags: ignoreversion
; Terminal/ web assets — recursive copy (see C10 block above). Ticket 0abd79df.
Source: "{#SrcTerminal}\*"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\Terminal"; Components: clarion111; Flags: ignoreversion recursesubdirs; Excludes: "*.cs,\mockups\*,\test\*"
Source: "{#SrcTaskBoard}\lifecycle-board.html"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\TaskLifecycleBoard"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcClarionIndexer}\clarion-indexer.exe"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcClarionIndexer}\clarion-indexer.pdb"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
; Third-party license notices — ships wherever the addin does; the obligation travels with the binaries.
Source: "{#SrcRepoRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcDocs}\ClarionAssistant-Guide.html"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\docs"; Components: clarion111 and docs; Flags: ignoreversion
; --- Clarion 11.1 LSP Server ---
#if HaveNodeExe
Source: "{#SrcNodeExe}"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\lsp-server"; Components: clarion111 and lsp; Flags: ignoreversion
; Node's own license bundle (V8, OpenSSL, ICU, zlib and the rest), shipped beside the binary
; it covers. Stable DestName so a Node upgrade replaces it instead of leaving the old one behind.
Source: "{#SrcRepoRoot}\third-party\node-v24.13.0-LICENSE.txt"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\lsp-server"; DestName: "node-LICENSE.txt"; Components: clarion111 and lsp; Flags: ignoreversion
#endif
#if HaveLsp
Source: "{#SrcLsp}\out\server\*"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\lsp-server\out\server"; Components: clarion111 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\out\common\*"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\lsp-server\out\common"; Components: clarion111 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-jsonrpc\*"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-jsonrpc"; Components: clarion111 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-languageserver\*"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-languageserver"; Components: clarion111 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-languageserver-protocol\*"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-languageserver-protocol"; Components: clarion111 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-languageserver-textdocument\*"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-languageserver-textdocument"; Components: clarion111 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-languageserver-types\*"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-languageserver-types"; Components: clarion111 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\xml2js\*"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\xml2js"; Components: clarion111 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\sax\*"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\sax"; Components: clarion111 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\xmlbuilder\*"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\xmlbuilder"; Components: clarion111 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\iconv-lite\*"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\iconv-lite"; Components: clarion111 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\safer-buffer\*"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\safer-buffer"; Components: clarion111 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
#endif

; --- Clarion 12 Addin ---
Source: "{#SrcC12}\ClarionAssistant.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcC12}\ClarionAssistant.pdb"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcC12}\ClarionAssistant.addin"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
; Hard <Reference> of ClarionAssistant.dll (copy-local) — see C10 note. Ticket 0abd79df.
Source: "{#SrcC12}\ClarionLsp.Contracts.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcC12}\Microsoft.Web.WebView2.Core.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcC12}\Microsoft.Web.WebView2.WinForms.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcC12}\Microsoft.Web.WebView2.Wpf.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcC12}\WebView2Loader.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcC12}\runtimes\win-x86\native\WebView2Loader.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\runtimes\win-x86\native"; Components: clarion12; Flags: ignoreversion
;   PdfPig - in-process PDF text extraction for DocGraph ingestion (#167). The UglyToad.*
;   wildcard covers the seven PdfPig assemblies; the five shims below are NOT inbox on .NET
;   Framework 4.8 and arrive via PdfPig package dependencies. Omitting any one fails at
;   RUNTIME on the first PDF import, not at build - so a missing entry here looks exactly
;   like the silent 'no documents found' bug it replaced. Mirrors the block in deploy.ps1.
Source: "{#SrcC12}\UglyToad.*.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcC12}\Microsoft.Bcl.HashCode.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcC12}\System.Buffers.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcC12}\System.Memory.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcC12}\System.Numerics.Vectors.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcC12}\System.Runtime.CompilerServices.Unsafe.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcC12}\Everything32.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcFts5}\System.Data.SQLite.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcFts5}\SQLite.Interop.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcFts5}\SQLite.Interop.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\x86"; Components: clarion12; Flags: ignoreversion
; Terminal/ web assets — recursive copy (see C10 block above). Ticket 0abd79df.
Source: "{#SrcTerminal}\*"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\Terminal"; Components: clarion12; Flags: ignoreversion recursesubdirs; Excludes: "*.cs,\mockups\*,\test\*"
Source: "{#SrcTaskBoard}\lifecycle-board.html"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\TaskLifecycleBoard"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcClarionIndexer}\clarion-indexer.exe"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcClarionIndexer}\clarion-indexer.pdb"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
; Third-party license notices — ships wherever the addin does; the obligation travels with the binaries.
Source: "{#SrcRepoRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcDocs}\ClarionAssistant-Guide.html"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\docs"; Components: clarion12 and docs; Flags: ignoreversion
; --- Clarion 12 LSP Server ---
#if HaveNodeExe
Source: "{#SrcNodeExe}"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\lsp-server"; Components: clarion12 and lsp; Flags: ignoreversion
; Node's own license bundle (V8, OpenSSL, ICU, zlib and the rest), shipped beside the binary
; it covers. Stable DestName so a Node upgrade replaces it instead of leaving the old one behind.
Source: "{#SrcRepoRoot}\third-party\node-v24.13.0-LICENSE.txt"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\lsp-server"; DestName: "node-LICENSE.txt"; Components: clarion12 and lsp; Flags: ignoreversion
#endif
#if HaveLsp
Source: "{#SrcLsp}\out\server\*"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\lsp-server\out\server"; Components: clarion12 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\out\common\*"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\lsp-server\out\common"; Components: clarion12 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-jsonrpc\*"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-jsonrpc"; Components: clarion12 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-languageserver\*"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-languageserver"; Components: clarion12 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-languageserver-protocol\*"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-languageserver-protocol"; Components: clarion12 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-languageserver-textdocument\*"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-languageserver-textdocument"; Components: clarion12 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\vscode-languageserver-types\*"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\vscode-languageserver-types"; Components: clarion12 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\xml2js\*"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\xml2js"; Components: clarion12 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\sax\*"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\sax"; Components: clarion12 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\xmlbuilder\*"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\xmlbuilder"; Components: clarion12 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\iconv-lite\*"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\iconv-lite"; Components: clarion12 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcLsp}\node_modules\safer-buffer\*"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\lsp-server\node_modules\safer-buffer"; Components: clarion12 and lsp; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

; --- Markdown Editor addin (third-party: msarson/ClarionMarkdownEditor, MIT) ---
; All four Clarion roots are listed together rather than interleaved with each version's block,
; because this payload is identical for every root and is NOT built per Clarion release — it is a
; prebuilt upstream zip staged by markdown-editor-sync\Sync-MarkdownEditor.ps1.
;
; Destination is accessory\addins\MarkdownEditor — its OWN folder, deliberately NOT a subfolder of
; ClarionAssistant. See the [Components] note: a duplicate Identity anywhere under accessory\addins
; stops the IDE starting.
;
; ignoreversion is REQUIRED, not incidental. Upstream never bumps the DLL's FileVersion resource
; (v1.2.0 still reports 1.0.2.0), so Inno's default version comparison would compare equal and
; behave unpredictably across releases. All freshness logic lives in ShouldInstallMarkdown, which
; reads the <Identity version> out of any already-installed .addin and declines to overwrite a copy
; NEWER than our pin — so a user tracking upstream directly is never silently downgraded.
#if HaveMarkdown && HaveMarkdownLicense
Source: "{#SrcMarkdown}\*"; DestDir: "{code:GetC10Path}\accessory\addins\MarkdownEditor"; Components: clarion10 and markdown; Check: ShouldInstallMarkdown('10'); Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcMarkdown}\*"; DestDir: "{code:GetC11Path}\accessory\addins\MarkdownEditor"; Components: clarion11 and markdown; Check: ShouldInstallMarkdown('11'); Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcMarkdown}\*"; DestDir: "{code:GetC111Path}\accessory\addins\MarkdownEditor"; Components: clarion111 and markdown; Check: ShouldInstallMarkdown('111'); Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcMarkdown}\*"; DestDir: "{code:GetC12Path}\accessory\addins\MarkdownEditor"; Components: clarion12 and markdown; Check: ShouldInstallMarkdown('12'); Flags: ignoreversion recursesubdirs createallsubdirs
#endif

; --- COM for Clarion: IDE Addin (installs to whichever Clarion version is selected — uses C12 path) ---
; ClarionCOMBrowser is a separate repo (see SrcComForClarion above) — skip if not present at compile time.
#if HaveComForClarion
Source: "{#SrcComForClarion}\ClarionCOMBrowser.dll"; DestDir: "{code:GetPrimaryClarionPath}\accessory\addins\ComForClarion"; Components: comforclarion\addin; Flags: ignoreversion
Source: "{#SrcComForClarion}\ClarionCOMBrowser.pdb"; DestDir: "{code:GetPrimaryClarionPath}\accessory\addins\ComForClarion"; Components: comforclarion\addin; Flags: ignoreversion
Source: "{#SrcComForClarion}\ClarionCOMBrowser.addin"; DestDir: "{code:GetPrimaryClarionPath}\accessory\addins\ComForClarion"; Components: comforclarion\addin; Flags: ignoreversion
Source: "{#SrcComForClarion}\Microsoft.Web.WebView2.Core.dll"; DestDir: "{code:GetPrimaryClarionPath}\accessory\addins\ComForClarion"; Components: comforclarion\addin; Flags: ignoreversion
Source: "{#SrcComForClarion}\Microsoft.Web.WebView2.WinForms.dll"; DestDir: "{code:GetPrimaryClarionPath}\accessory\addins\ComForClarion"; Components: comforclarion\addin; Flags: ignoreversion
Source: "{#SrcComForClarion}\Microsoft.Web.WebView2.Wpf.dll"; DestDir: "{code:GetPrimaryClarionPath}\accessory\addins\ComForClarion"; Components: comforclarion\addin; Flags: ignoreversion
Source: "{#SrcComForClarion}\WebView2Loader.dll"; DestDir: "{code:GetPrimaryClarionPath}\accessory\addins\ComForClarion"; Components: comforclarion\addin; Flags: ignoreversion
Source: "{#SrcComForClarion}\runtimes\win-x86\native\WebView2Loader.dll"; DestDir: "{code:GetPrimaryClarionPath}\accessory\addins\ComForClarion\runtimes\win-x86\native"; Components: comforclarion\addin; Flags: ignoreversion
#endif

; --- COM for Clarion: UltimateCOM Templates & Class ---
; Class/template sources and the Clarion-12-built DLLs are independent external dependencies
; (see SrcUltimateClasses/SrcUltimateTemplates/SrcTemplateDlls above) — each guarded separately.
#if HaveUltimateClasses
Source: "{#SrcUltimateClasses}\UltimateCOM.inc"; DestDir: "{code:GetPrimaryClarionPath}\accessory\libsrc\win"; Components: comforclarion\templates; Flags: ignoreversion
Source: "{#SrcUltimateClasses}\UltimateCOM.clw"; DestDir: "{code:GetPrimaryClarionPath}\accessory\libsrc\win"; Components: comforclarion\templates; Flags: ignoreversion
#endif
#if HaveUltimateTemplates
Source: "{#SrcUltimateTemplates}\UltimateCOM.tpl"; DestDir: "{code:GetPrimaryClarionPath}\accessory\template\win"; Components: comforclarion\templates; Flags: ignoreversion
#endif
#if HaveTemplateDlls
Source: "{#SrcTemplateDlls}\UCSelectCOM.dll"; DestDir: "{code:GetPrimaryClarionPath}\accessory\template\win"; Components: comforclarion\templates; Flags: ignoreversion
Source: "{#SrcTemplateDlls}\UCSelectCOMProgID.dll"; DestDir: "{code:GetPrimaryClarionPath}\accessory\template\win"; Components: comforclarion\templates; Flags: ignoreversion
Source: "{#SrcTemplateDlls}\UTFileCopy.dll"; DestDir: "{code:GetPrimaryClarionPath}\accessory\template\win"; Components: comforclarion\templates; Flags: ignoreversion
#endif

; --- COM for Clarion: Documentation ---
#if HaveComDocs
Source: "{#SrcComDocs}\*"; DestDir: "{code:GetPrimaryClarionPath}\accessory\resources\ComForClarionDocumentation"; Components: comforclarion\docs; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

; --- COM for Clarion: ClarionCOM Tooling ---
#if HaveClarionCOM
Source: "{#SrcClarionCOM}\Template\*"; DestDir: "{userappdata}\ClarionCOM\Templates"; Components: comforclarion\tooling; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcClarionCOM}\.claude\scripts\*"; DestDir: "{userappdata}\ClarionCOM\scripts"; Components: comforclarion\tooling; Flags: ignoreversion
Source: "{#SrcClarionCOM}\GenerateClarionMetadata.ps1"; DestDir: "{userappdata}\ClarionCOM\scripts"; Components: comforclarion\tooling; Flags: ignoreversion
Source: "{#SrcClarionCOM}\GenerateReadmeHTML.ps1"; DestDir: "{userappdata}\ClarionCOM\scripts"; Components: comforclarion\tooling; Flags: ignoreversion
Source: "{#SrcClarionCOM}\ParseCOMInterface.ps1"; DestDir: "{userappdata}\ClarionCOM\scripts"; Components: comforclarion\tooling; Flags: ignoreversion
Source: "{#SrcClarionCOM}\install-skills.bat"; DestDir: "{userappdata}\ClarionCOM"; Components: comforclarion\tooling; Flags: ignoreversion
Source: "{#SrcClarionCOM}\install-skills.ps1"; DestDir: "{userappdata}\ClarionCOM"; Components: comforclarion\tooling; Flags: ignoreversion
Source: "{#SrcClarionCOM}\install-env.bat"; DestDir: "{userappdata}\ClarionCOM"; Components: comforclarion\tooling; Flags: ignoreversion
Source: "{#SrcClarionCOM}\install-env.ps1"; DestDir: "{userappdata}\ClarionCOM"; Components: comforclarion\tooling; Flags: ignoreversion
Source: "{#SrcClarionCOM}\version.txt"; DestDir: "{userappdata}\ClarionCOM"; Components: comforclarion\tooling; Flags: ignoreversion
#endif

; --- Clarion Assistant Plugin ---
; The plugin is NO LONGER bundled here. configure.ps1 (see [Run]) registers the
; real GitHub marketplace and installs it:
;   claude plugin marketplace add ClarionLive/clarionassistant-marketplace
;   claude plugin install clarion-assistant@clarionassistant-marketplace --scope user
; Claude Code git-clones it to
;   %USERPROFILE%\.claude\plugins\marketplaces\clarionassistant-marketplace\...
; which is the exact path the ClarionAssistant runtime reads. This makes the
; plugin a genuine, `claude plugin marketplace update`-able marketplace instead
; of a static installer copy. Repo source of truth: marketplace\ (published to
; the GitHub repo via installer\publish-marketplace-to-github.ps1).

; --- Blank dictionary template ---
; blank.dct / ClassModels are pulled from the packaging machine's own %APPDATA%\clarionassistant
; (populated by running ClarionAssistant locally) — skip if that machine hasn't generated it yet.
#if HaveBlankDct
Source: "{#SrcBlankDct}\blank.dct"; DestDir: "{userappdata}\clarionassistant"; Components: plugin\skills; Flags: ignoreversion

; --- Default class model templates ---
Source: "{#SrcBlankDct}\ClassModels\*.inc"; DestDir: "{userappdata}\clarionassistant\ClassModels"; Components: plugin\skills; Flags: onlyifdoesntexist
Source: "{#SrcBlankDct}\ClassModels\*.clw"; DestDir: "{userappdata}\clarionassistant\ClassModels"; Components: plugin\skills; Flags: onlyifdoesntexist
#endif

; --- Claude Code Quality Agents ---
#if HaveAgents
Source: "{#SrcAgents}\code-reviewer.md"; DestDir: "{%USERPROFILE}\.claude\agents"; Components: agents; Flags: onlyifdoesntexist
Source: "{#SrcAgents}\verifier.md"; DestDir: "{%USERPROFILE}\.claude\agents"; Components: agents; Flags: onlyifdoesntexist
Source: "{#SrcAgents}\debugger.md"; DestDir: "{%USERPROFILE}\.claude\agents"; Components: agents; Flags: onlyifdoesntexist
Source: "{#SrcAgents}\security-auditor.md"; DestDir: "{%USERPROFILE}\.claude\agents"; Components: agents; Flags: onlyifdoesntexist
Source: "{#SrcAgents}\test-designer.md"; DestDir: "{%USERPROFILE}\.claude\agents"; Components: agents; Flags: onlyifdoesntexist
Source: "{#SrcAgents}\devils-advocate.md"; DestDir: "{%USERPROFILE}\.claude\agents"; Components: agents; Flags: onlyifdoesntexist
#endif

; --- Pre-loaded DocGraph Database ---
#ifexist SrcInstaller + "\docgraph.db"
Source: "{#SrcInstaller}\docgraph.db"; DestDir: "{userappdata}\ClarionAssistant"; Components: docgraph; Flags: ignoreversion
#endif

; --- User Guide ---
Source: "{#SrcDocs}\ClarionAssistant-Guide.html"; DestDir: "{app}"; Components: docs; Flags: ignoreversion

; --- Post-install configuration script ---
; Installed to {app} (not {tmp}) so the SECOND [Run] entry, which runs as the
; original NON-elevated user via `runasoriginaluser`, can read it -- {tmp} lives
; under the elevated account and is not reliably accessible to the de-elevated user.
Source: "{#SrcInstaller}\configure.ps1"; DestDir: "{app}"; Flags: ignoreversion

; --- CLAUDE.md reference ---
; Sourced from the BUNDLED prompt, not a hand-maintained copy. There used to be an
; installer\CLAUDE.md here; it silently drifted to a 133-line snapshot that was missing
; eight whole tool sections and still documented the removed `open_app` tool. Same bug
; class as the 51-missing-tools prompt drift. One source of truth -- do not reintroduce
; a second copy of this document.
Source: "{#SrcTerminal}\clarion-assistant-prompt.md"; DestDir: "{%USERPROFILE}\.claude"; DestName: "clarion-assistant-reference.md"; Flags: ignoreversion

; ============================================================
; DIRECTORIES
; ============================================================

[Dirs]
; DocGraphService.GetDefaultDbPath() (the runtime's actual lookup path) uses
; Environment.SpecialFolder.ApplicationData, i.e. Roaming AppData -- matches {userappdata}
; below and the docgraph.db [Files] entry above, NOT {localappdata}.
Name: "{userappdata}\ClarionAssistant"
; Marketplace dirs are created by `claude plugin marketplace add` (git clone),
; not the installer — see the [Files] note above and configure.ps1.
Name: "{%USERPROFILE}\.claude\agents"; Components: agents
Name: "{userappdata}\clarionassistant"; Components: plugin\skills
Name: "{userappdata}\ClarionCOM"; Components: comforclarion\tooling
Name: "{userappdata}\ClarionCOM\Templates"; Components: comforclarion\tooling
Name: "{userappdata}\ClarionCOM\scripts"; Components: comforclarion\tooling

; ============================================================
; POST-INSTALL
; ============================================================

[Run]
; 1. Configure Claude Code settings + env (runs elevated, like the rest of Setup).
;    Plugin install is a SEPARATE step (below) so it can run as the original user.
Filename: "powershell.exe"; \
  Parameters: "-ExecutionPolicy Bypass -File ""{app}\configure.ps1"" -ClarionRoot ""{code:GetPrimaryClarionPath}"" -DocGraphDb ""{userappdata}\ClarionAssistant\docgraph.db"""; \
  StatusMsg: "Configuring Claude Code settings..."; \
  Flags: runhidden waituntilterminated

; 2. Register + install the Clarion Assistant plugin from GitHub AS THE ORIGINAL USER.
;    runasoriginaluser => `claude plugin install --scope user` lands in the actual
;    user's profile (where ClarionAssistant reads it), not the elevated admin's, and
;    we never exec a user-writable `claude` binary from the elevated installer context.
Filename: "powershell.exe"; \
  Parameters: "-ExecutionPolicy Bypass -File ""{app}\configure.ps1"" -InstallPlugin"; \
  Components: plugin; \
  StatusMsg: "Installing Clarion Assistant plugin from GitHub..."; \
  Flags: runasoriginaluser runhidden waituntilterminated

; Run install-env.bat for ClarionCOM
Filename: "{userappdata}\ClarionCOM\install-env.bat"; \
  Parameters: """{code:GetPrimaryClarionPath}"""; \
  Components: comforclarion\tooling; \
  StatusMsg: "Configuring ClarionCOM environment..."; \
  Flags: runhidden waituntilterminated

; Register UltimateCOM template
Filename: "{code:GetPrimaryClarionPath}\bin\ClarionCL.exe"; \
  Parameters: "/tr ""{code:GetPrimaryClarionPath}\accessory\template\win\UltimateCOM.tpl"""; \
  Components: comforclarion\templates; \
  Description: "Register UltimateCOM template with the Clarion IDE"; \
  Flags: postinstall waituntilterminated runhidden unchecked

; View the user guide
Filename: "{app}\ClarionAssistant-Guide.html"; \
  Description: "View the User Guide"; \
  Components: docs; \
  Flags: nowait postinstall skipifsilent shellexec unchecked

[UninstallDelete]
; Clean up generated files per version
Type: filesandordirs; Name: "{code:GetC10Path}\accessory\addins\ClarionAssistant"; Components: clarion10
Type: filesandordirs; Name: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11
Type: filesandordirs; Name: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111
Type: filesandordirs; Name: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12

; ============================================================
; PASCAL SCRIPT
; ============================================================

[Code]
var
  C10Path, C11Path, C111Path, C12Path: string;
  // ADDITIONAL installations of the SAME release, ';'-separated. A developer can have e.g. two
  // Clarion 12 trees; the four rows below can only name one each, so the extras are mirrored after
  // install (MirrorExtras). Empty = just the one folder on that row, which is the common case.
  C10Extra, C11Extra, C111Extra, C12Extra: string;
  ClarionPathPage: TInputQueryWizardPage;
  AddBtn0, AddBtn1, AddBtn2, AddBtn3: TNewButton;
  // Markdown Editor install verdict, computed once per Clarion release and then frozen.
  // Indexed by MarkdownIndexFor: 0=C10, 1=C11, 2=C11.1, 3=C12. Declared up here rather than
  // beside the functions so the scope is unambiguous. See ShouldInstallMarkdown for why the
  // freezing is load-bearing and not merely an optimisation.
  MarkdownDecided: array[0..3] of Boolean;
  MarkdownVerdict: array[0..3] of Boolean;

function GetC10Path(Param: string): string; begin Result := C10Path; end;
function GetC11Path(Param: string): string; begin Result := C11Path; end;
function GetC111Path(Param: string): string; begin Result := C111Path; end;
function GetC12Path(Param: string): string; begin Result := C12Path; end;

function IsC10Detected: Boolean; begin Result := (C10Path <> '') and DirExists(C10Path); end;
function IsC11Detected: Boolean; begin Result := (C11Path <> '') and DirExists(C11Path); end;
function IsC111Detected: Boolean; begin Result := (C111Path <> '') and DirExists(C111Path); end;
function IsC12Detected: Boolean; begin Result := (C12Path <> '') and DirExists(C12Path); end;

// Return the highest available Clarion version path (for COM, templates, etc.)
function GetPrimaryClarionPath(Param: string): string;
begin
  if (C12Path <> '') and DirExists(C12Path) then Result := C12Path
  else if (C111Path <> '') and DirExists(C111Path) then Result := C111Path
  else if (C11Path <> '') and DirExists(C11Path) then Result := C11Path
  else if (C10Path <> '') and DirExists(C10Path) then Result := C10Path
  else Result := 'C:\Clarion12';
end;

// ============================================================
// Markdown Editor — only-if-newer install decision
// ============================================================
// The Markdown Editor is a THIRD-PARTY addin (msarson/ClarionMarkdownEditor, MIT) that the user may
// already have, installed from upstream's own release zip or via ClarionAddinFinder. We redistribute
// a pinned copy so CA users get a current one by default, but we must never silently DOWNGRADE
// somebody who is ahead of our pin.
//
// The comparison has to read <Identity version="..."/> out of the .addin manifest. It cannot use the
// DLL: upstream does not bump the FileVersion resource, so v1.2.0 and v1.0.2 both report 1.0.2.0.
// That also means Inno's own "replace only if newer" file-version logic is blind here — hence
// ignoreversion on the [Files] entries and all the real logic in this function.

function ReadAddinIdentityVersion(AddinPath: String): String;
var
  Raw: AnsiString;
  T: String;
  P, Q: Integer;
begin
  Result := '';
  if not FileExists(AddinPath) then Exit;
  if not LoadStringFromFile(AddinPath, Raw) then Exit;
  T := String(Raw);
  P := Pos('<Identity', T);
  if P = 0 then Exit;
  T := Copy(T, P, Length(T) - P + 1);
  P := Pos('version="', T);
  if P = 0 then Exit;
  T := Copy(T, P + Length('version="'), Length(T));
  Q := Pos('"', T);
  if Q = 0 then Exit;
  Result := Copy(T, 1, Q - 1);
end;

// Consumes the leading dotted component of S and returns it as an integer, leaving the remainder.
// A missing or non-numeric component reads as 0, so '1.2' and '1.2.0' compare equal.
function NextVersionPart(var S: String): Integer;
var
  P: Integer;
begin
  P := Pos('.', S);
  if P = 0 then
  begin
    Result := StrToIntDef(S, 0);
    S := '';
  end
  else
  begin
    Result := StrToIntDef(Copy(S, 1, P - 1), 0);
    S := Copy(S, P + 1, Length(S));
  end;
end;

// -1 if A < B, 0 if equal, 1 if A > B. Component-wise, so 1.10.0 correctly beats 1.9.0 — which a
// string comparison would get backwards.
function CompareDottedVersion(A, B: String): Integer;
var
  PA, PB: Integer;
begin
  Result := 0;
  while (A <> '') or (B <> '') do
  begin
    PA := NextVersionPart(A);
    PB := NextVersionPart(B);
    if PA > PB then begin Result := 1; Exit; end;
    if PA < PB then begin Result := -1; Exit; end;
  end;
end;

function MarkdownRootFor(Which: String): String;
begin
  if Which = '10' then Result := C10Path
  else if Which = '11' then Result := C11Path
  else if Which = '111' then Result := C111Path
  else Result := C12Path;
end;

function MarkdownIndexFor(Which: String): Integer;
begin
  if Which = '10' then Result := 0
  else if Which = '11' then Result := 1
  else if Which = '111' then Result := 2
  else Result := 3;
end;

// The actual decision. Do NOT wire this to a [Files] Check directly -- go through
// ShouldInstallMarkdown, which freezes the answer. See the comment there.
function DecideInstallMarkdown(Param: String): Boolean;
var
  Root, Dir, AddinPath, Installed: String;
begin
  Root := MarkdownRootFor(Param);
  if Root = '' then
  begin
    Result := False;
    Exit;
  end;

  Dir := Root + '\accessory\addins\MarkdownEditor';
  AddinPath := Dir + '\ClarionMarkdownEditor.addin';

  if not FileExists(AddinPath) then
  begin
    Log('Markdown[' + Param + ']: no existing install -> installing pinned {#MarkdownPinVersion}');
    Result := True;
    Exit;
  end;

  // A manifest with no assembly beside it is not an install, it is wreckage. The IDE parses the
  // .addin, tries to LoadFrom the DLL next to it, and fails startup outright with "Could not load
  // file or assembly 'ClarionMarkdownEditor.dll'". Version comparison is meaningless in that state
  // because there is nothing to downgrade, so repair unconditionally.
  //
  // This branch is ALSO the recovery path for machines already broken by the 5.8 wildcard defect
  // described in ShouldInstallMarkdown: their lone .addin reports the pinned version, so without
  // this check the version comparison below would read them as up to date and skip the payload on
  // every future install, leaving them broken permanently.
  if not FileExists(Dir + '\ClarionMarkdownEditor.dll') then
  begin
    Log('Markdown[' + Param + ']: .addin present but ClarionMarkdownEditor.dll is MISSING (broken install) -> repairing with pinned {#MarkdownPinVersion}');
    Result := True;
    Exit;
  end;

  Installed := ReadAddinIdentityVersion(AddinPath);
  if Installed = '' then
  begin
    // Present but with no readable Identity: the IDE cannot load that manifest either, so replacing
    // it is a repair rather than a downgrade.
    Log('Markdown[' + Param + ']: existing .addin has no readable <Identity version> (malformed) -> installing pinned {#MarkdownPinVersion}');
    Result := True;
    Exit;
  end;

  if CompareDottedVersion('{#MarkdownPinVersion}', Installed) > 0 then
  begin
    Log('Markdown[' + Param + ']: installed ' + Installed + ' is older than pinned {#MarkdownPinVersion} -> upgrading');
    Result := True;
  end
  else
  begin
    Log('Markdown[' + Param + ']: installed ' + Installed + ' is >= pinned {#MarkdownPinVersion} -> leaving the user''s copy alone');
    Result := False;
  end;
end;

// [Files] Check function. Param is the Clarion release discriminator ('10','11','111','12') rather
// than a path, so the decision reads the same globals the wizard populated and does not depend on
// constant expansion inside a Check parameter.
//
// FREEZING THE VERDICT IS LOAD-BEARING, NOT AN OPTIMISATION (5.8.1 hotfix).
// A [Files] entry whose Source is a wildcard evaluates its Check function ONCE PER EXPANDED FILE,
// at install time. ClarionMarkdownEditor.addin sorts alphabetically FIRST within {#SrcMarkdown}\*,
// so on a clean root 5.8 installed the manifest, and then every remaining file of the SAME wildcard
// re-ran this decision, found the .addin that had just been written reporting the pinned version,
// took the "installed >= pinned -> leave the user's copy alone" branch, and was SKIPPED. The user
// was left with a folder holding exactly one file -- the manifest -- and a Clarion IDE that would
// not start. Reported on Discord against 5.8 and reproduced locally: empty the folder, run the
// installer, exactly one file lands.
//
// Caching the first answer fixes it because the first call necessarily happens BEFORE this run has
// written anything, so the verdict reflects the state the user actually arrived with. Re-reading
// the destination mid-wildcard is what made the gate destroy its own precondition.
function ShouldInstallMarkdown(Param: String): Boolean;
var
  Idx: Integer;
begin
  Idx := MarkdownIndexFor(Param);
  if not MarkdownDecided[Idx] then
  begin
    MarkdownVerdict[Idx] := DecideInstallMarkdown(Param);
    MarkdownDecided[Idx] := True;
  end;
  Result := MarkdownVerdict[Idx];
end;

// Where we remember the paths a previous run actually installed to. See SavedClarionPath.
const
  PathMemoryKey = 'Software\ClarionAssistant\InstallPaths';

// A path this installer used last time for the given Clarion release, or '' if there isn't one.
//
// The registry probe below reads what SoftVelocity's OWN installer registered, which is not
// necessarily the tree the developer actually launches: a second copy of the same release, or one
// started with /Configdir= against a different settings folder, is registered under neither. A
// developer in that position corrects the path in the wizard and — before this — had it discarded,
// so every release put the addin back in the tree they don't run. That failure is close to silent:
// the IDE simply keeps loading the old addin, and the symptoms get reported against a version that
// was replaced weeks ago (issue #142 spent its first round diagnosing exactly that).
//
// Validated with DirExists so a remembered path that has since been moved or deleted falls through
// to detection instead of pinning the installer to somewhere that no longer exists.
function SavedClarionPath(ValueName: string): string;
var
  S: string;
begin
  Result := '';
  if RegQueryStringValue(HKCU, PathMemoryKey, ValueName, S) and (S <> '') and DirExists(S) then
    Result := S;
end;

// A remembered list of extra same-version folders, dropping any that no longer look like a Clarion
// install. Filtering here (rather than at copy time) keeps a deleted or moved tree from silently
// reappearing in the summary and from being re-created by robocopy's ForceDirectories.
function SavedExtraPaths(ValueName: string): string;
var
  Raw, Item, Kept: string;
  P: Integer;
begin
  Result := '';
  if not RegQueryStringValue(HKCU, PathMemoryKey, ValueName, Raw) then Exit;
  Kept := '';
  while Raw <> '' do
  begin
    P := Pos(';', Raw);
    if P > 0 then
    begin
      Item := Trim(Copy(Raw, 1, P - 1));
      Raw := Copy(Raw, P + 1, Length(Raw));
    end
    else
    begin
      Item := Trim(Raw);
      Raw := '';
    end;
    if (Item <> '') and DirExists(AddBackslash(Item) + 'bin') then
    begin
      if Kept = '' then Kept := Item else Kept := Kept + ';' + Item;
    end;
  end;
  Result := Kept;
end;

// Auto-detect Clarion paths: remembered choice first, then registry, then common locations.
procedure DetectClarionPaths;
var
  Path: string;
begin
  C12Extra := SavedExtraPaths('Clarion12Extra');
  C111Extra := SavedExtraPaths('Clarion11.1Extra');
  C11Extra := SavedExtraPaths('Clarion11Extra');
  C10Extra := SavedExtraPaths('Clarion10Extra');

  // Clarion 12
  C12Path := SavedClarionPath('Clarion12');
  if C12Path = '' then
  begin
    if RegQueryStringValue(HKLM32, 'SOFTWARE\SoftVelocity\Clarion12', 'root', Path) and DirExists(Path) then
      C12Path := Path
    else if DirExists('C:\Clarion12') then C12Path := 'C:\Clarion12'
    else if DirExists('C:\Clarion12d') then C12Path := 'C:\Clarion12d';
  end;

  // Clarion 11.1 — a DISTINCT release from 11.0, with its own binding DLLs (see deploy.ps1's
  // Directory.Build.props note). Must never share a path with C11Path below.
  C111Path := SavedClarionPath('Clarion11.1');
  if C111Path = '' then
  begin
    if RegQueryStringValue(HKLM32, 'SOFTWARE\SoftVelocity\Clarion11.1', 'root', Path) and DirExists(Path) then
      C111Path := Path
    else if RegQueryStringValue(HKLM32, 'SOFTWARE\SoftVelocity\Clarion111', 'root', Path) and DirExists(Path) then
      C111Path := Path
    else if DirExists('C:\Clarion11.1') then C111Path := 'C:\Clarion11.1'
    else if DirExists('d:\Clarion11.1EE') then C111Path := 'd:\Clarion11.1EE';
  end;

  // Clarion 11 (11.0)
  C11Path := SavedClarionPath('Clarion11');
  if C11Path = '' then
  begin
    if RegQueryStringValue(HKLM32, 'SOFTWARE\SoftVelocity\Clarion11', 'root', Path) and DirExists(Path) then
      C11Path := Path
    else if DirExists('C:\Clarion11') then C11Path := 'C:\Clarion11'
    else if DirExists('C:\Clarion11-13372') then C11Path := 'C:\Clarion11-13372';
  end;

  // Clarion 10
  C10Path := SavedClarionPath('Clarion10');
  if C10Path = '' then
  begin
    if RegQueryStringValue(HKLM32, 'SOFTWARE\SoftVelocity\Clarion10', 'root', Path) and DirExists(Path) then
      C10Path := Path
    else if DirExists('C:\Clarion10') then C10Path := 'C:\Clarion10'
    else if DirExists('C:\Clarion10v8') then C10Path := 'C:\Clarion10v8';
  end;
end;

// Check if Claude Code CLI is installed
function IsClaudeCodeInstalled: Boolean;
var
  ResultCode: Integer;
begin
  // Check npm global install
  Result := FileExists(ExpandConstant('{userappdata}\npm\claude.cmd'));
  if Result then Exit;

  // Check standalone CLI install
  Result := FileExists(ExpandConstant('{%USERPROFILE}\.claude\local\claude.exe'));
  if Result then Exit;

  // Check WinGet install
  Result := FileExists(ExpandConstant('{localappdata}\Microsoft\WinGet\Links\claude.exe'));
  if Result then Exit;

  // Fallback: try PATH
  Result := Exec('cmd.exe', '/c claude --version >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := Result and (ResultCode = 0);
end;

// Check if WebView2 Runtime is installed
function IsWebView2Installed: Boolean;
var
  Version: string;
begin
  Result := RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);
  if not Result then
    Result := RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);
end;

// ============================================================
// Row identity, version detection, and ADDITIONAL folders per version
// ============================================================
// Each row installs a DIFFERENT build of the addin: SrcC10..SrcC12 are separate outputs, each
// compiled against its own Clarion's ICSharpCode.*/CWBinding/CommonSources (see the HintPaths in
// ClarionAssistant.csproj). So a row is not a free-form path slot — putting a Clarion 12 folder in
// the Clarion 10 row ships the C10 build into a C12 tree, which does not load. A user asked exactly
// that on Discord ("may I use the 3 entrys ... no matter if the prompt says Clarion10?"), which is
// what these three additions answer: clearer wording, a per-row "+" for MORE folders of the SAME
// version, and a version check on whatever gets typed or picked.

function ExpectedVersionForRow(Row: Integer): string;
begin
  case Row of
    0: Result := '12';
    1: Result := '11.1';
    2: Result := '11';
    3: Result := '10';
  else
    Result := '';
  end;
end;

// The Clarion release actually sitting in Path: '12' | '11.1' | '11' | '10', or '' when unknown.
// bin\ClarionCL.exe carries it cleanly (verified across all four local installs: 12.0.0.14000,
// 11.1.0.13815, 11.0.0.13630, 10.0.0.12799), and the MINOR word is what separates 11.1 from 11.0.
// Do NOT use bin\ICSharpCode.SharpDevelop.dll — it is 2.1.0.2447 in every one of them.
function DetectedClarionVersion(Path: string): string;
var
  Exe: string;
  VerMS, VerLS: Cardinal;
  Major, Minor: Integer;
begin
  Result := '';
  if Trim(Path) = '' then Exit;
  Exe := AddBackslash(Path) + 'bin\ClarionCL.exe';
  if not FileExists(Exe) then Exe := AddBackslash(Path) + 'bin\Clarion.exe';
  if not FileExists(Exe) then Exit;
  if not GetVersionNumbers(Exe, VerMS, VerLS) then Exit;
  Major := VerMS shr 16;
  Minor := VerMS and $FFFF;
  if Major = 12 then Result := '12'
  else if Major = 11 then
  begin
    if Minor >= 1 then Result := '11.1' else Result := '11';
  end
  else if Major = 10 then Result := '10';
end;

// True = the caller may proceed with this path on this row. Silent when the folder's version can't
// be determined — an unreadable/repackaged install must not be un-installable, so we only ever warn
// on a CONFIDENT mismatch. Defaults to "No" because continuing is nearly always a mistake.
function ConfirmVersionMatch(Row: Integer; Path: string): Boolean;
var
  Found, Want: string;
begin
  Result := True;
  Want := ExpectedVersionForRow(Row);
  Found := DetectedClarionVersion(Path);
  if (Found = '') or (Found = Want) then Exit;
  Result := (MsgBox('This folder looks like Clarion ' + Found + ', but it was entered as a Clarion ' + Want + ' folder:' + #13#10 +
                    Path + #13#10#13#10 +
                    'Each Clarion version needs its OWN build of the addin, compiled against that version''s IDE files. ' +
                    'Installing the Clarion ' + Want + ' build into a Clarion ' + Found + ' installation will not work.' + #13#10#13#10 +
                    'Put this folder in the "Clarion ' + Found + ' folder" row instead. If you have more than one Clarion ' +
                    Found + ' installed, use the "+" button on that row to add the second one.' + #13#10#13#10 +
                    'Continue anyway?', mbError, MB_YESNO or MB_DEFBUTTON2) = IDYES);
end;

function GetExtrasFor(Row: Integer): string;
begin
  case Row of
    0: Result := C12Extra;
    1: Result := C111Extra;
    2: Result := C11Extra;
    3: Result := C10Extra;
  else
    Result := '';
  end;
end;

procedure SetExtrasFor(Row: Integer; Value: string);
begin
  case Row of
    0: C12Extra := Value;
    1: C111Extra := Value;
    2: C11Extra := Value;
    3: C10Extra := Value;
  end;
end;

function GetPrimaryFor(Row: Integer): string;
begin
  case Row of
    0: Result := C12Path;
    1: Result := C111Path;
    2: Result := C11Path;
    3: Result := C10Path;
  else
    Result := '';
  end;
end;

procedure SetPrimaryFor(Row: Integer; Value: string);
begin
  case Row of
    0: C12Path := Value;
    1: C111Path := Value;
    2: C11Path := Value;
    3: C10Path := Value;
  end;
end;

// ---- ';'-separated path lists -------------------------------------------------------------------
// A row's edit field IS the list: the folders are visible and editable right there, so a wrong entry
// is corrected or deleted like any other text. (First design put extras in a label with the paths in
// a tooltip and an all-or-nothing "Clear extras" — John's review: invisible, unintuitive, and no way
// to fix ONE bad entry out of three. The field is the better home.)
// Entry 0 is the PRIMARY: that is what [Files] installs into via {code:GetCxxPath}. Entries 1..n are
// copied from the primary afterwards by MirrorExtras.

function PathCount(List: string): Integer;
var
  P: Integer;
begin
  Result := 0;
  while List <> '' do
  begin
    P := Pos(';', List);
    if P > 0 then
    begin
      if Trim(Copy(List, 1, P - 1)) <> '' then Result := Result + 1;
      List := Copy(List, P + 1, Length(List));
    end
    else
    begin
      if Trim(List) <> '' then Result := Result + 1;
      List := '';
    end;
  end;
end;

// 0-based; '' when Index is past the end. Skips blank entries so "a;;b" behaves as "a;b".
function PathItem(List: string; Index: Integer): string;
var
  P, N: Integer;
  Item: string;
begin
  Result := '';
  N := 0;
  while List <> '' do
  begin
    P := Pos(';', List);
    if P > 0 then
    begin
      Item := Trim(Copy(List, 1, P - 1));
      List := Copy(List, P + 1, Length(List));
    end
    else
    begin
      Item := Trim(List);
      List := '';
    end;
    if Item = '' then Continue;
    if N = Index then
    begin
      Result := Item;
      Exit;
    end;
    N := N + 1;
  end;
end;

// Everything after the first entry, re-joined — the folders that get a copy after install.
function PathRest(List: string): string;
var
  i: Integer;
  Item: string;
begin
  Result := '';
  i := 1;
  Item := PathItem(List, i);
  while Item <> '' do
  begin
    if Result = '' then Result := Item else Result := Result + ';' + Item;
    i := i + 1;
    Item := PathItem(List, i);
  end;
end;

// True when Candidate is already somewhere in List (case-insensitive, trailing-slash tolerant).
function PathListContains(List, Candidate: string): Boolean;
var
  i: Integer;
  Item, Want: string;
begin
  Result := False;
  Want := Uppercase(RemoveBackslashUnlessRoot(Trim(Candidate)));
  i := 0;
  Item := PathItem(List, i);
  while Item <> '' do
  begin
    if Uppercase(RemoveBackslashUnlessRoot(Item)) = Want then
    begin
      Result := True;
      Exit;
    end;
    i := i + 1;
    Item := PathItem(List, i);
  end;
end;

function ComposeField(Primary, Extras: string): string;
begin
  if Trim(Primary) = '' then Result := ''
  else if Trim(Extras) = '' then Result := Trim(Primary)
  else Result := Trim(Primary) + ';' + Trim(Extras);
end;

// "+" — the row's ONLY button: pick a Clarion installation folder and append it to the field as a
// ';'-separated entry. It handles an empty field too (first entry, no separator), which is why the
// separate "Browse..." button it replaced was pure duplication — John's review: "why have two buttons
// when one simple one will do?" Everything it adds is ordinary editable text, so a mistake is
// corrected or deleted in place.
procedure AddFolderForRow(Row: Integer);
var
  Dir, Field, Want: string;
begin
  Want := ExpectedVersionForRow(Row);
  Field := Trim(ClarionPathPage.Values[Row]);

  // Start the picker at the last folder already listed, so a sibling install is a click or two away.
  Dir := PathItem(Field, PathCount(Field) - 1);
  if Dir = '' then Dir := 'C:\';

  if not BrowseForFolder('Select a Clarion ' + Want + ' installation folder:', Dir, False) then Exit;
  Dir := Trim(Dir);
  if Dir = '' then Exit;

  if not DirExists(AddBackslash(Dir) + 'bin') then
  begin
    MsgBox('That folder does not look like a Clarion installation (no "bin" directory):' + #13#10 + Dir, mbError, MB_OK);
    Exit;
  end;
  if PathListContains(Field, Dir) then
  begin
    MsgBox('That folder is already listed on the Clarion ' + Want + ' row.', mbInformation, MB_OK);
    Exit;
  end;
  if not ConfirmVersionMatch(Row, Dir) then Exit;

  if Field = '' then Field := Dir else Field := Field + ';' + Dir;
  ClarionPathPage.Values[Row] := Field;
end;

procedure AddBtn0Click(Sender: TObject); begin AddFolderForRow(0); end;
procedure AddBtn1Click(Sender: TObject); begin AddFolderForRow(1); end;
procedure AddBtn2Click(Sender: TObject); begin AddFolderForRow(2); end;
procedure AddBtn3Click(Sender: TObject); begin AddFolderForRow(3); end;

procedure InitializeWizard;
var
  DetectedMsg: string;
  EditWidth: Integer;
begin
  DetectClarionPaths;

  DetectedMsg := 'Each row installs the addin built for THAT Clarion version — the builds are not' + #13#10 +
    'interchangeable, so please don''t point a row at a different version''s folder.' + #13#10#13#10 +
    'Auto-detected paths are shown below. Correct any that are wrong, or empty a row to' + #13#10 +
    'skip it. Same version installed more than once? List the folders separated by' + #13#10 +
    'semicolons — "+" picks one for you. All of them get the addin.';

  ClarionPathPage := CreateInputQueryPage(wpLicense,
    'Clarion Installation Paths',
    'Where are your Clarion versions installed?',
    DetectedMsg);

  ClarionPathPage.Add('Clarion 12 folder:', False);
  ClarionPathPage.Add('Clarion 11.1 folder:', False);
  ClarionPathPage.Add('Clarion 11 folder:', False);
  ClarionPathPage.Add('Clarion 10 folder:', False);

  // Shrink the edit fields just enough for the single "+" button: 6 gap + 24 button + 4 slack.
  // (Browse... used to sit here too; "+" does the same job including the first entry, so the
  // field gets those 81px back.)
  EditWidth := ClarionPathPage.Edits[0].Width - 34;

  ClarionPathPage.Edits[0].Width := EditWidth;
  ClarionPathPage.Edits[1].Width := EditWidth;
  ClarionPathPage.Edits[2].Width := EditWidth;
  ClarionPathPage.Edits[3].Width := EditWidth;

  // "+" — the one button per row: pick a folder and append it to that row's list.
  AddBtn0 := TNewButton.Create(WizardForm);
  AddBtn0.Parent := ClarionPathPage.Edits[0].Parent;
  AddBtn0.Caption := '+';
  AddBtn0.Hint := 'Add a Clarion 12 installation folder to this row';
  AddBtn0.ShowHint := True;
  AddBtn0.Left := ClarionPathPage.Edits[0].Left + EditWidth + 6;
  AddBtn0.Top := ClarionPathPage.Edits[0].Top;
  AddBtn0.Width := 24;
  AddBtn0.Height := ClarionPathPage.Edits[0].Height;
  AddBtn0.OnClick := @AddBtn0Click;

  AddBtn1 := TNewButton.Create(WizardForm);
  AddBtn1.Parent := ClarionPathPage.Edits[1].Parent;
  AddBtn1.Caption := '+';
  AddBtn1.Hint := 'Add a Clarion 11.1 installation folder to this row';
  AddBtn1.ShowHint := True;
  AddBtn1.Left := ClarionPathPage.Edits[1].Left + EditWidth + 6;
  AddBtn1.Top := ClarionPathPage.Edits[1].Top;
  AddBtn1.Width := 24;
  AddBtn1.Height := ClarionPathPage.Edits[1].Height;
  AddBtn1.OnClick := @AddBtn1Click;

  AddBtn2 := TNewButton.Create(WizardForm);
  AddBtn2.Parent := ClarionPathPage.Edits[2].Parent;
  AddBtn2.Caption := '+';
  AddBtn2.Hint := 'Add a Clarion 11 installation folder to this row';
  AddBtn2.ShowHint := True;
  AddBtn2.Left := ClarionPathPage.Edits[2].Left + EditWidth + 6;
  AddBtn2.Top := ClarionPathPage.Edits[2].Top;
  AddBtn2.Width := 24;
  AddBtn2.Height := ClarionPathPage.Edits[2].Height;
  AddBtn2.OnClick := @AddBtn2Click;

  AddBtn3 := TNewButton.Create(WizardForm);
  AddBtn3.Parent := ClarionPathPage.Edits[3].Parent;
  AddBtn3.Caption := '+';
  AddBtn3.Hint := 'Add a Clarion 10 installation folder to this row';
  AddBtn3.ShowHint := True;
  AddBtn3.Left := ClarionPathPage.Edits[3].Left + EditWidth + 6;
  AddBtn3.Top := ClarionPathPage.Edits[3].Top;
  AddBtn3.Width := 24;
  AddBtn3.Height := ClarionPathPage.Edits[3].Height;
  AddBtn3.OnClick := @AddBtn3Click;

  // Pre-fill each row with its remembered list: primary first, then any additional folders for the
  // same release. No separate summary widget — the field is the list.
  ClarionPathPage.Values[0] := ComposeField(C12Path, C12Extra);
  ClarionPathPage.Values[1] := ComposeField(C111Path, C111Extra);
  ClarionPathPage.Values[2] := ComposeField(C11Path, C11Extra);
  ClarionPathPage.Values[3] := ComposeField(C10Path, C10Extra);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  AnyValid: Boolean;
  Row, Idx: Integer;
  Field, Item, Want: string;
begin
  Result := True;

  if CurPageID = ClarionPathPage.ID then
  begin
    // Each row may now hold SEVERAL ';'-separated folders for that one Clarion release. Validate
    // every entry, then split the row: entry 0 is the primary that [Files] installs into, the rest
    // are mirrored from it after install.
    AnyValid := False;
    for Row := 0 to 3 do
    begin
      Want := ExpectedVersionForRow(Row);
      Field := Trim(ClarionPathPage.Values[Row]);
      ClarionPathPage.Values[Row] := Field;
      if Field = '' then
      begin
        SetPrimaryFor(Row, '');
        SetExtrasFor(Row, '');
        Continue;
      end;
      Idx := 0;
      Item := PathItem(Field, Idx);
      while Item <> '' do
      begin
        if not DirExists(AddBackslash(Item) + 'bin') then
        begin
          MsgBox('This Clarion ' + Want + ' folder does not look valid (no "bin" directory):' + #13#10 +
                 Item + #13#10#13#10 +
                 'Correct it, remove it from the row, or empty the row to skip Clarion ' + Want + '.',
                 mbError, MB_OK);
          Result := False;
          Exit;
        end;
        // Wrong-row paths install the wrong build — see ConfirmVersionMatch.
        if not ConfirmVersionMatch(Row, Item) then
        begin
          Result := False;
          Exit;
        end;
        Idx := Idx + 1;
        Item := PathItem(Field, Idx);
      end;
      SetPrimaryFor(Row, PathItem(Field, 0));
      SetExtrasFor(Row, PathRest(Field));
      AnyValid := True;
    end;

    if not AnyValid then
    begin
      MsgBox('At least one Clarion version path is required.' + #13#10 +
             'Please enter the path to your Clarion installation.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;

  // Validate on Components page: warn if a Clarion component is checked but path is empty
  if CurPageID = wpSelectComponents then
  begin
    if WizardIsComponentSelected('clarion12') and (C12Path = '') then
    begin
      MsgBox('Clarion 12 addin is selected but no Clarion 12 path was specified.' + #13#10 +
             'Go back and enter the path, or uncheck Clarion 12.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if WizardIsComponentSelected('clarion11') and (C11Path = '') then
    begin
      MsgBox('Clarion 11 addin is selected but no Clarion 11 path was specified.' + #13#10 +
             'Go back and enter the path, or uncheck Clarion 11.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if WizardIsComponentSelected('clarion111') and (C111Path = '') then
    begin
      MsgBox('Clarion 11.1 addin is selected but no Clarion 11.1 path was specified.' + #13#10 +
             'Go back and enter the path, or uncheck Clarion 11.1.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if WizardIsComponentSelected('clarion10') and (C10Path = '') then
    begin
      MsgBox('Clarion 10 addin is selected but no Clarion 10 path was specified.' + #13#10 +
             'Go back and enter the path, or uncheck Clarion 10.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

function InitializeSetup: Boolean;
var
  Msg: string;
begin
  Result := True;
  Msg := '';

  if not IsWebView2Installed then
    Msg := Msg + '- Microsoft Edge WebView2 Runtime is required but not installed.' + #13#10 +
           '  Download from: https://developer.microsoft.com/en-us/microsoft-edge/webview2/' + #13#10#13#10;

  if not IsClaudeCodeInstalled then
    Msg := Msg + '- Claude Code CLI is required but was not detected.' + #13#10 +
           '  Install with:  winget install Anthropic.ClaudeCode' + #13#10 +
           '  Or from:       https://claude.ai/download' + #13#10#13#10;


  if Msg <> '' then
  begin
    Msg := 'The following prerequisites were not found:' + #13#10#13#10 + Msg +
           'You can continue the installation, but Clarion Assistant will not' + #13#10 +
           'function until these are installed.' + #13#10#13#10 +
           'Continue anyway?';
    Result := (MsgBox(Msg, mbConfirmation, MB_YESNO) = IDYES);
  end;
end;

// Only remove the addin from Clarion versions being reinstalled
procedure CurPageChanged(CurPageID: Integer);
var
  i: Integer;
  Cap: string;
  HasPath: Boolean;
begin
  // When entering Components page, auto-check versions that have a path, uncheck those without
  if CurPageID = wpSelectComponents then
  begin
    for i := 0 to WizardForm.ComponentsList.Items.Count - 1 do
    begin
      Cap := WizardForm.ComponentsList.ItemCaption[i];
      if Cap = 'Clarion 12 Addin' then
      begin
        HasPath := (C12Path <> '') and DirExists(C12Path);
        WizardForm.ComponentsList.Checked[i] := HasPath;
        WizardForm.ComponentsList.ItemEnabled[i] := HasPath;
      end;
      if Cap = 'Clarion 11 Addin' then
      begin
        HasPath := (C11Path <> '') and DirExists(C11Path);
        WizardForm.ComponentsList.Checked[i] := HasPath;
        WizardForm.ComponentsList.ItemEnabled[i] := HasPath;
      end;
      if Cap = 'Clarion 11.1 Addin' then
      begin
        HasPath := (C111Path <> '') and DirExists(C111Path);
        WizardForm.ComponentsList.Checked[i] := HasPath;
        WizardForm.ComponentsList.ItemEnabled[i] := HasPath;
      end;
      if Cap = 'Clarion 10 Addin' then
      begin
        HasPath := (C10Path <> '') and DirExists(C10Path);
        WizardForm.ComponentsList.Checked[i] := HasPath;
        WizardForm.ComponentsList.ItemEnabled[i] := HasPath;
      end;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  NeedsRestart := False;

  Log('PrepareToInstall: C10Path=' + C10Path);
  Log('PrepareToInstall: C11Path=' + C11Path);
  Log('PrepareToInstall: C111Path=' + C111Path);
  Log('PrepareToInstall: C12Path=' + C12Path);
  Log('PrepareToInstall: C10 selected=' + IntToStr(Ord(WizardIsComponentSelected('clarion10'))));
  Log('PrepareToInstall: C11 selected=' + IntToStr(Ord(WizardIsComponentSelected('clarion11'))));
  Log('PrepareToInstall: C111 selected=' + IntToStr(Ord(WizardIsComponentSelected('clarion111'))));
  Log('PrepareToInstall: C12 selected=' + IntToStr(Ord(WizardIsComponentSelected('clarion12'))));

  if WizardIsComponentSelected('clarion10') and (C10Path <> '') and DirExists(C10Path + '\accessory\addins\ClarionAssistant') then
  begin
    Log('Removing previous C10 addin: ' + C10Path);
    DelTree(C10Path + '\accessory\addins\ClarionAssistant', True, True, True);
  end;

  if WizardIsComponentSelected('clarion11') and (C11Path <> '') and DirExists(C11Path + '\accessory\addins\ClarionAssistant') then
  begin
    Log('Removing previous C11 addin: ' + C11Path);
    DelTree(C11Path + '\accessory\addins\ClarionAssistant', True, True, True);
  end;

  if WizardIsComponentSelected('clarion111') and (C111Path <> '') and DirExists(C111Path + '\accessory\addins\ClarionAssistant') then
  begin
    Log('Removing previous C11.1 addin: ' + C111Path);
    DelTree(C111Path + '\accessory\addins\ClarionAssistant', True, True, True);
  end;

  if WizardIsComponentSelected('clarion12') and (C12Path <> '') and DirExists(C12Path + '\accessory\addins\ClarionAssistant') then
  begin
    Log('Removing previous C12 addin: ' + C12Path);
    DelTree(C12Path + '\accessory\addins\ClarionAssistant', True, True, True);
  end;
end;

// Remember which tree each Clarion release was actually installed into, so the next release's
// wizard offers that instead of re-deriving it from the registry. See SavedClarionPath for why
// the registry alone is not enough (issue #142).
//
// Written at ssPostInstall, not when the wizard page is left: a path is only worth remembering
// once files have actually been written to it, and a run the user cancels should change nothing.
//
// Only NON-EMPTY paths are saved. An empty field means "skip this Clarion", and persisting that
// would turn one release's skip into a permanent one — a developer who later installs that release
// would find the addin silently absent, with nothing on screen explaining why. Left unsaved, the
// field simply falls back to detection next time, which is the pre-existing behaviour.
procedure SaveClarionPaths;
begin
  if WizardIsComponentSelected('clarion12') and (C12Path <> '') then
    RegWriteStringValue(HKCU, PathMemoryKey, 'Clarion12', C12Path);
  if WizardIsComponentSelected('clarion111') and (C111Path <> '') then
    RegWriteStringValue(HKCU, PathMemoryKey, 'Clarion11.1', C111Path);
  if WizardIsComponentSelected('clarion11') and (C11Path <> '') then
    RegWriteStringValue(HKCU, PathMemoryKey, 'Clarion11', C11Path);
  if WizardIsComponentSelected('clarion10') and (C10Path <> '') then
    RegWriteStringValue(HKCU, PathMemoryKey, 'Clarion10', C10Path);

  // Extra same-version folders, remembered for the same reason as the primaries: a developer with two
  // Clarion 12 trees installs every release, and re-adding the second one by hand each time is exactly
  // the kind of step that gets forgotten — after which that install silently keeps an old addin.
  // Written UNCONDITIONALLY (including empty) so clearing the list actually sticks, unlike the
  // primaries above where an empty field means "skip", not "forget".
  if WizardIsComponentSelected('clarion12') then
    RegWriteStringValue(HKCU, PathMemoryKey, 'Clarion12Extra', C12Extra);
  if WizardIsComponentSelected('clarion111') then
    RegWriteStringValue(HKCU, PathMemoryKey, 'Clarion11.1Extra', C111Extra);
  if WizardIsComponentSelected('clarion11') then
    RegWriteStringValue(HKCU, PathMemoryKey, 'Clarion11Extra', C11Extra);
  if WizardIsComponentSelected('clarion10') then
    RegWriteStringValue(HKCU, PathMemoryKey, 'Clarion10Extra', C10Extra);
end;

// ============================================================
// ADDITIONAL same-version folders: mirror the installed addin into each one
// ============================================================
// [Files] destinations are static — one DestDir per Source line, resolved through {code:GetCxxPath}
// — so a second folder for the same Clarion release cannot be expressed there. Instead we let the
// row install normally and then copy the finished folder across. That is exactly the manual
// workaround ("copy accessory\addins\ClarionAssistant to the other install"), just automated, and it
// is safe precisely BECAUSE both targets are the same release and take the same build.
//
// robocopy rather than a hand-rolled recursive copy: the tree has several levels (Terminal,
// TaskLifecycleBoard, runtimes, lsp-server\node_modules, x86) and /MIR also removes a stale earlier
// copy in the target, which is the same cleanup PrepareToInstall does for the primary paths. Exit
// codes 0-7 are success for robocopy; 8+ is a real failure.
function MirrorAddin(SrcRoot, DstRoot: string): Boolean;
var
  Src, Dst: string;
  ResultCode: Integer;
begin
  Result := False;
  Src := AddBackslash(SrcRoot) + 'accessory\addins\ClarionAssistant';
  Dst := AddBackslash(DstRoot) + 'accessory\addins\ClarionAssistant';
  if not DirExists(Src) then
  begin
    Log('MirrorAddin: source missing, nothing to copy: ' + Src);
    Exit;
  end;
  if CompareText(RemoveBackslashUnlessRoot(Src), RemoveBackslashUnlessRoot(Dst)) = 0 then Exit;
  ForceDirectories(Dst);
  Result := Exec(ExpandConstant('{sys}\robocopy.exe'),
                 '"' + RemoveBackslashUnlessRoot(Src) + '" "' + RemoveBackslashUnlessRoot(Dst) + '" ' +
                 '/MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1',
                 '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode < 8);
  Log('MirrorAddin ' + Src + ' -> ' + Dst + ' rc=' + IntToStr(ResultCode) + ' ok=' + IntToStr(Ord(Result)));
end;

// Copy this row's installed addin into every extra folder registered for it.
procedure MirrorExtras(Row: Integer; ComponentName: string);
var
  List, Item, Primary: string;
  P, Done, Failed: Integer;
begin
  List := GetExtrasFor(Row);
  Primary := GetPrimaryFor(Row);
  if (List = '') or (Primary = '') then Exit;
  if not WizardIsComponentSelected(ComponentName) then
  begin
    Log('MirrorExtras: component ' + ComponentName + ' not selected — skipping ' + List);
    Exit;
  end;
  Done := 0; Failed := 0;
  while List <> '' do
  begin
    P := Pos(';', List);
    if P > 0 then
    begin
      Item := Trim(Copy(List, 1, P - 1));
      List := Copy(List, P + 1, Length(List));
    end
    else
    begin
      Item := Trim(List);
      List := '';
    end;
    if Item = '' then Continue;
    if MirrorAddin(Primary, Item) then Done := Done + 1 else Failed := Failed + 1;
  end;
  Log('MirrorExtras row ' + IntToStr(Row) + ': copied=' + IntToStr(Done) + ' failed=' + IntToStr(Failed));
  if Failed > 0 then
    MsgBox('Clarion ' + ExpectedVersionForRow(Row) + ': ' + IntToStr(Failed) +
           ' extra folder(s) could not be updated.' + #13#10#13#10 +
           'The main folder installed normally. Check the install log for "MirrorAddin", and make sure ' +
           'Clarion is closed in the other installation(s).', mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Extras first: SaveClarionPaths persists the list, and a run that failed to copy should still
    // remember what the developer asked for so the next release retries it.
    MirrorExtras(0, 'clarion12');
    MirrorExtras(1, 'clarion111');
    MirrorExtras(2, 'clarion11');
    MirrorExtras(3, 'clarion10');
    SaveClarionPaths;
  end;
end;
