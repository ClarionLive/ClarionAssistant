# Markdown Editor sync

Pins and stages the third-party Markdown Editor addin that the installer redistributes.

- **Upstream:** [msarson/ClarionMarkdownEditor](https://github.com/msarson/ClarionMarkdownEditor), MIT.
- **Pin:** `markdown-snapshot.json` (`resolvedTag`, `resolvedIdentityVersion`, `assetSha256`).
- **Payload:** `..\.markdown-build\<tag>\` — gitignored, regenerated from the pin.

## Bumping the pin

```powershell
.\Sync-MarkdownEditor.ps1 -Tag v1.3.0
```

Then update **both** defines in `installer\ClarionAssistant.iss` — they are hardcoded, exactly as
`SrcLsp` is:

```
#define SrcMarkdown SrcBase + "\.markdown-build\v1.3.0"
#define MarkdownPinVersion "1.3.0"
```

Rebuild the installer and confirm the ISCC log has no `shipping WITHOUT` line for the Markdown
Editor. (`build-installer.ps1` fails the build on those, so a clean exit is the proof.)

## Three things that are easy to get wrong

**The DLL's version resource is frozen.** Upstream does not bump it: v1.2.0 still reports
`1.0.2.0`, byte-identical to v1.0.2. So Inno's built-in "replace only if newer" comparison cannot
tell one release from another and must never be relied on here. The authoritative version is the
`<Identity version="..."/>` attribute in `ClarionMarkdownEditor.addin`, which upstream *does*
maintain. The sync parses it and records it as `resolvedIdentityVersion`; the installer's
`ShouldInstallMarkdown` compares that against whatever the user already has and declines to
overwrite a newer copy.

**It must install to `accessory\addins\MarkdownEditor`.** Clarion scans every subfolder under
`accessory\addins`, and two copies sharing the `ClarionMarkdownEditor` Identity fail IDE startup
outright with *"Identity name used by multiple addins"*. Putting it under `ClarionAssistant\`
would break the IDE for anyone who already had the addin installed properly.

**The release ZIP contains no LICENSE.** MIT requires the copyright and permission notice to
travel with every redistributed copy, so the sync fetches `LICENSE` from the repo at the pinned
tag and stages it as `LICENSE-ClarionMarkdownEditor.txt`. The installer treats its absence as a
build failure, not a warning — see `HaveMarkdownLicense` in the `.iss`.

## Not wired into deploy.ps1

Deliberately. `deploy.ps1` installs *our* addin to local Clarion trees; this payload is a
third-party addin a developer may be managing themselves (via upstream's zip or
[ClarionAddinFinder](https://github.com/msarson/ClarionAddinFinder)). Having a dev deploy silently
replace it would reintroduce the downgrade the installer's version check exists to prevent. Run
the sync when bumping the pin; let the installer be the thing that distributes it.
