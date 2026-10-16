# Contributing

Use Windows 11 and the .NET 10 SDK. Run `dotnet build GlassRea.slnx` and `dotnet test tests\GlassRea.Tests` before opening a pull request; both must pass with no warnings.

Keep the split: the service (`GlassRea.Service`) owns capture, storage and the firewall; the app (`GlassRea.App`) only asks it for data over the pipe; anything both need lives in `GlassRea.Core`. Add a test for any change to storage, attribution, limits, alert text or parsing, and make sure it fails without your change before you trust it.

Text the app shows uses plain words, sentence case and no em dashes. Numbers are always Latin digits, whatever the PC's language. Don't add third-party native libraries: Smart App Control blocks unsigned ones, which is why the tray icon, SQLite binding and folder picker are written against Windows directly.

Never include your `data.db`, real host names, device lists, MAC addresses or screenshots with personal traffic in commits or issues. Report security issues privately (see SECURITY.md). Contributions are made under the repository's MIT license.
