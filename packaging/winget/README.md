# Publishing NetRoute to winget

`winget install NetRoute` needs three things that only exist once a release is public, so these
manifests carry placeholders until then.

1. **Publish the installer somewhere with a stable URL.** Build it with
   `scripts\BUILD-INSTALLER.cmd`, then upload `dist\NetRoute-Setup-<version>.exe` (a GitHub
   release asset is the usual choice).
2. **Fill in `NetRoute.NetRoute.installer.yaml`**: `InstallerUrl`, and `InstallerSha256` from the
   hash the build script prints (or `Get-FileHash`). Set `PackageVersion` in all three files.
3. **Sign the installer** if you can (`BUILD-INSTALLER.cmd -CertThumbprint <thumbprint>`).
   Unsigned packages are accepted, but every user sees SmartScreen's warning.

Then validate and submit:

```
winget validate --manifest packaging\winget
winget install --manifest packaging\winget      # installs your local build, to test the manifest
```

Submit by opening a pull request against [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs)
with the three files under `manifests/n/NetRoute/NetRoute/<version>/`, or use
[wingetcreate](https://github.com/microsoft/winget-create):

```
wingetcreate update NetRoute.NetRoute --version <version> --urls <installer url> --submit
```

## In-app updates

The service checks for a newer version once a day, but only when `UpdateFeedUrl` is set in
`%ProgramData%\NetRoute\config.json`. It expects a small JSON document:

```json
{ "version": "1.1.0", "url": "https://example.com/NetRoute-Setup-1.1.0.exe", "notes": "What changed." }
```

Finding something newer shows a line in the app with a link. NetRoute never downloads or installs
an update by itself.
