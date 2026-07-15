; Clarion Assistant v5.3 Installer
; Inno Setup 6 Script
; Supports Clarion 10, 11, 12 — user picks which version(s) to install

#define MyAppName "Clarion Assistant"
#define MyAppVersion "5.3"
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
; The directory containing this .iss file itself (SourcePath already ends in "\").
#define SrcInstaller Copy(SourcePath, 1, Len(SourcePath)-1)

; ---- Optional-source presence flags ----
; Each probes a sentinel FILE (never a bare directory — ISPP #ifexist/FileExists return FALSE
; for directories). A missing source drops its [Files] entries and emits exactly one warning
; below, so the packaging log always shows what was omitted from the installer.
#define HaveNodeExe FileExists(SrcNodeExe)
#define HaveLsp FileExists(SrcLsp + "\out\server\src\server.js")
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
Source: "{#SrcDocs}\ClarionAssistant-Guide.html"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\docs"; Components: clarion10 and docs; Flags: ignoreversion
; --- Clarion 10 LSP Server ---
#if HaveNodeExe
Source: "{#SrcNodeExe}"; DestDir: "{code:GetC10Path}\accessory\addins\ClarionAssistant\lsp-server"; Components: clarion10 and lsp; Flags: ignoreversion
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
Source: "{#SrcC11}\Everything32.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcFts5}\System.Data.SQLite.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcFts5}\SQLite.Interop.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcFts5}\SQLite.Interop.dll"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\x86"; Components: clarion11; Flags: ignoreversion
; Terminal/ web assets — recursive copy (see C10 block above). Ticket 0abd79df.
Source: "{#SrcTerminal}\*"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\Terminal"; Components: clarion11; Flags: ignoreversion recursesubdirs; Excludes: "*.cs,\mockups\*,\test\*"
Source: "{#SrcTaskBoard}\lifecycle-board.html"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\TaskLifecycleBoard"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcClarionIndexer}\clarion-indexer.exe"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcClarionIndexer}\clarion-indexer.pdb"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant"; Components: clarion11; Flags: ignoreversion
Source: "{#SrcDocs}\ClarionAssistant-Guide.html"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\docs"; Components: clarion11 and docs; Flags: ignoreversion
; --- Clarion 11 LSP Server ---
#if HaveNodeExe
Source: "{#SrcNodeExe}"; DestDir: "{code:GetC11Path}\accessory\addins\ClarionAssistant\lsp-server"; Components: clarion11 and lsp; Flags: ignoreversion
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
Source: "{#SrcC11_1}\Everything32.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcFts5}\System.Data.SQLite.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcFts5}\SQLite.Interop.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcFts5}\SQLite.Interop.dll"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\x86"; Components: clarion111; Flags: ignoreversion
; Terminal/ web assets — recursive copy (see C10 block above). Ticket 0abd79df.
Source: "{#SrcTerminal}\*"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\Terminal"; Components: clarion111; Flags: ignoreversion recursesubdirs; Excludes: "*.cs,\mockups\*,\test\*"
Source: "{#SrcTaskBoard}\lifecycle-board.html"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\TaskLifecycleBoard"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcClarionIndexer}\clarion-indexer.exe"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcClarionIndexer}\clarion-indexer.pdb"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant"; Components: clarion111; Flags: ignoreversion
Source: "{#SrcDocs}\ClarionAssistant-Guide.html"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\docs"; Components: clarion111 and docs; Flags: ignoreversion
; --- Clarion 11.1 LSP Server ---
#if HaveNodeExe
Source: "{#SrcNodeExe}"; DestDir: "{code:GetC111Path}\accessory\addins\ClarionAssistant\lsp-server"; Components: clarion111 and lsp; Flags: ignoreversion
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
Source: "{#SrcC12}\Everything32.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcFts5}\System.Data.SQLite.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcFts5}\SQLite.Interop.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcFts5}\SQLite.Interop.dll"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\x86"; Components: clarion12; Flags: ignoreversion
; Terminal/ web assets — recursive copy (see C10 block above). Ticket 0abd79df.
Source: "{#SrcTerminal}\*"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\Terminal"; Components: clarion12; Flags: ignoreversion recursesubdirs; Excludes: "*.cs,\mockups\*,\test\*"
Source: "{#SrcTaskBoard}\lifecycle-board.html"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\TaskLifecycleBoard"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcClarionIndexer}\clarion-indexer.exe"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcClarionIndexer}\clarion-indexer.pdb"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant"; Components: clarion12; Flags: ignoreversion
Source: "{#SrcDocs}\ClarionAssistant-Guide.html"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\docs"; Components: clarion12 and docs; Flags: ignoreversion
; --- Clarion 12 LSP Server ---
#if HaveNodeExe
Source: "{#SrcNodeExe}"; DestDir: "{code:GetC12Path}\accessory\addins\ClarionAssistant\lsp-server"; Components: clarion12 and lsp; Flags: ignoreversion
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
Source: "{#SrcInstaller}\CLAUDE.md"; DestDir: "{%USERPROFILE}\.claude"; DestName: "clarion-assistant-reference.md"; Flags: ignoreversion

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
  ClarionPathPage: TInputQueryWizardPage;
  BrowseBtn0, BrowseBtn1, BrowseBtn2, BrowseBtn3: TNewButton;

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

// Auto-detect Clarion paths from registry and common locations
procedure DetectClarionPaths;
var
  Path: string;
begin
  // Clarion 12
  C12Path := '';
  if RegQueryStringValue(HKLM32, 'SOFTWARE\SoftVelocity\Clarion12', 'root', Path) and DirExists(Path) then
    C12Path := Path
  else if DirExists('C:\Clarion12') then C12Path := 'C:\Clarion12'
  else if DirExists('C:\Clarion12d') then C12Path := 'C:\Clarion12d';

  // Clarion 11.1 — a DISTINCT release from 11.0, with its own binding DLLs (see deploy.ps1's
  // Directory.Build.props note). Must never share a path with C11Path below.
  C111Path := '';
  if RegQueryStringValue(HKLM32, 'SOFTWARE\SoftVelocity\Clarion11.1', 'root', Path) and DirExists(Path) then
    C111Path := Path
  else if RegQueryStringValue(HKLM32, 'SOFTWARE\SoftVelocity\Clarion111', 'root', Path) and DirExists(Path) then
    C111Path := Path
  else if DirExists('C:\Clarion11.1') then C111Path := 'C:\Clarion11.1'
  else if DirExists('d:\Clarion11.1EE') then C111Path := 'd:\Clarion11.1EE';

  // Clarion 11 (11.0)
  C11Path := '';
  if RegQueryStringValue(HKLM32, 'SOFTWARE\SoftVelocity\Clarion11', 'root', Path) and DirExists(Path) then
    C11Path := Path
  else if DirExists('C:\Clarion11') then C11Path := 'C:\Clarion11'
  else if DirExists('C:\Clarion11-13372') then C11Path := 'C:\Clarion11-13372';

  // Clarion 10
  C10Path := '';
  if RegQueryStringValue(HKLM32, 'SOFTWARE\SoftVelocity\Clarion10', 'root', Path) and DirExists(Path) then
    C10Path := Path
  else if DirExists('C:\Clarion10') then C10Path := 'C:\Clarion10'
  else if DirExists('C:\Clarion10v8') then C10Path := 'C:\Clarion10v8';
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

procedure BrowseForPath(EditIndex: Integer);
var
  Dir: string;
begin
  Dir := ClarionPathPage.Values[EditIndex];
  if Dir = '' then Dir := 'C:\';
  if BrowseForFolder('Select Clarion installation folder:', Dir, False) then
    ClarionPathPage.Values[EditIndex] := Dir;
end;

procedure BrowseBtn0Click(Sender: TObject); begin BrowseForPath(0); end;
procedure BrowseBtn1Click(Sender: TObject); begin BrowseForPath(1); end;
procedure BrowseBtn2Click(Sender: TObject); begin BrowseForPath(2); end;
procedure BrowseBtn3Click(Sender: TObject); begin BrowseForPath(3); end;

procedure InitializeWizard;
var
  DetectedMsg: string;
  EditWidth: Integer;
begin
  DetectClarionPaths;

  DetectedMsg := 'Select the Clarion installation folders.' + #13#10#13#10 +
    'Auto-detected paths are shown below. Edit any path that is incorrect,' + #13#10 +
    'or leave a field empty to skip that version.';

  ClarionPathPage := CreateInputQueryPage(wpLicense,
    'Clarion Installation Paths',
    'Where are your Clarion versions installed?',
    DetectedMsg);

  ClarionPathPage.Add('Clarion 12 folder:', False);
  ClarionPathPage.Add('Clarion 11.1 folder:', False);
  ClarionPathPage.Add('Clarion 11 folder:', False);
  ClarionPathPage.Add('Clarion 10 folder:', False);

  // Shrink edit fields to make room for browse buttons
  EditWidth := ClarionPathPage.Edits[0].Width - 85;

  // Add Browse buttons next to each field
  BrowseBtn0 := TNewButton.Create(WizardForm);
  BrowseBtn0.Parent := ClarionPathPage.Edits[0].Parent;
  BrowseBtn0.Caption := 'Browse...';
  BrowseBtn0.Left := ClarionPathPage.Edits[0].Left + EditWidth + 6;
  BrowseBtn0.Top := ClarionPathPage.Edits[0].Top;
  BrowseBtn0.Width := 75;
  BrowseBtn0.Height := ClarionPathPage.Edits[0].Height;
  BrowseBtn0.OnClick := @BrowseBtn0Click;
  ClarionPathPage.Edits[0].Width := EditWidth;

  BrowseBtn1 := TNewButton.Create(WizardForm);
  BrowseBtn1.Parent := ClarionPathPage.Edits[1].Parent;
  BrowseBtn1.Caption := 'Browse...';
  BrowseBtn1.Left := ClarionPathPage.Edits[1].Left + EditWidth + 6;
  BrowseBtn1.Top := ClarionPathPage.Edits[1].Top;
  BrowseBtn1.Width := 75;
  BrowseBtn1.Height := ClarionPathPage.Edits[1].Height;
  BrowseBtn1.OnClick := @BrowseBtn1Click;
  ClarionPathPage.Edits[1].Width := EditWidth;

  BrowseBtn2 := TNewButton.Create(WizardForm);
  BrowseBtn2.Parent := ClarionPathPage.Edits[2].Parent;
  BrowseBtn2.Caption := 'Browse...';
  BrowseBtn2.Left := ClarionPathPage.Edits[2].Left + EditWidth + 6;
  BrowseBtn2.Top := ClarionPathPage.Edits[2].Top;
  BrowseBtn2.Width := 75;
  BrowseBtn2.Height := ClarionPathPage.Edits[2].Height;
  BrowseBtn2.OnClick := @BrowseBtn2Click;
  ClarionPathPage.Edits[2].Width := EditWidth;

  BrowseBtn3 := TNewButton.Create(WizardForm);
  BrowseBtn3.Parent := ClarionPathPage.Edits[3].Parent;
  BrowseBtn3.Caption := 'Browse...';
  BrowseBtn3.Left := ClarionPathPage.Edits[3].Left + EditWidth + 6;
  BrowseBtn3.Top := ClarionPathPage.Edits[3].Top;
  BrowseBtn3.Width := 75;
  BrowseBtn3.Height := ClarionPathPage.Edits[3].Height;
  BrowseBtn3.OnClick := @BrowseBtn3Click;
  ClarionPathPage.Edits[3].Width := EditWidth;

  // Pre-fill with detected paths
  ClarionPathPage.Values[0] := C12Path;
  ClarionPathPage.Values[1] := C111Path;
  ClarionPathPage.Values[2] := C11Path;
  ClarionPathPage.Values[3] := C10Path;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  AnyValid: Boolean;
begin
  Result := True;

  if CurPageID = ClarionPathPage.ID then
  begin
    // Read user-edited paths
    C12Path := ClarionPathPage.Values[0];
    C111Path := ClarionPathPage.Values[1];
    C11Path := ClarionPathPage.Values[2];
    C10Path := ClarionPathPage.Values[3];

    // Validate non-empty paths
    AnyValid := False;

    if C12Path <> '' then
    begin
      if not DirExists(C12Path + '\bin') then
      begin
        MsgBox('Clarion 12 path does not appear valid (no "bin" directory):' + #13#10 + C12Path + #13#10#13#10 +
               'Leave the field empty to skip Clarion 12, or correct the path.', mbError, MB_OK);
        Result := False;
        Exit;
      end;
      AnyValid := True;
    end;

    if C111Path <> '' then
    begin
      if not DirExists(C111Path + '\bin') then
      begin
        MsgBox('Clarion 11.1 path does not appear valid (no "bin" directory):' + #13#10 + C111Path + #13#10#13#10 +
               'Leave the field empty to skip Clarion 11.1, or correct the path.', mbError, MB_OK);
        Result := False;
        Exit;
      end;
      AnyValid := True;
    end;

    if C11Path <> '' then
    begin
      if not DirExists(C11Path + '\bin') then
      begin
        MsgBox('Clarion 11 path does not appear valid (no "bin" directory):' + #13#10 + C11Path + #13#10#13#10 +
               'Leave the field empty to skip Clarion 11, or correct the path.', mbError, MB_OK);
        Result := False;
        Exit;
      end;
      AnyValid := True;
    end;

    if C10Path <> '' then
    begin
      if not DirExists(C10Path + '\bin') then
      begin
        MsgBox('Clarion 10 path does not appear valid (no "bin" directory):' + #13#10 + C10Path + #13#10#13#10 +
               'Leave the field empty to skip Clarion 10, or correct the path.', mbError, MB_OK);
        Result := False;
        Exit;
      end;
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
