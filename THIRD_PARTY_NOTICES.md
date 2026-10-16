# Third-party notices

This repository contains only Glass-Rea's own source. Building it restores the packages below from NuGet, and the setup it builds redistributes their runtime files. Each keeps its own license.

| Package | Version | License |
|---|---|---|
| Microsoft.WindowsAppSDK (and the `Microsoft.WindowsAppSDK.*` and `Microsoft.Web.WebView2` packages it brings) | 2.5.1 | Microsoft Software License Terms, Windows App SDK (shipped in the package as `license.txt`) |
| Microsoft.Graphics.Win2D | 1.4.0 | Microsoft Software License Terms, Win2D: https://www.microsoft.com/web/webpi/eula/eula_win2d_10012014.htm |
| Microsoft.Diagnostics.Tracing.TraceEvent | 4.0.0 | MIT, Copyright (c) Microsoft Corporation |
| ManagedEsent | 2.0.0 | MIT, Copyright (c) Microsoft Corporation |
| Microsoft.Extensions.Hosting | 10.0.13 | MIT, Copyright (c) Microsoft Corporation |
| Microsoft.Extensions.Hosting.WindowsServices | 10.0.13 | MIT, Copyright (c) Microsoft Corporation |
| System.Management | 10.0.13 | MIT, Copyright (c) Microsoft Corporation |
| System.ServiceProcess.ServiceController | 10.0.13 | MIT, Copyright (c) Microsoft Corporation |

Test-only, not redistributed: xunit 2.9.3 (Apache-2.0), xunit.runner.visualstudio 3.1.5 (Apache-2.0), Microsoft.NET.Test.Sdk 18.11.0 (MIT).

The service is hosted by the .NET 10 runtime, which setup installs from Microsoft when it's missing (MIT, .NET Foundation). SQLite is Windows' own `winsqlite3.dll`; SQLite itself is in the public domain.

## MIT License (for the packages marked MIT)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
