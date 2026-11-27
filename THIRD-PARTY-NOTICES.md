# Third-party notices

RevitAgent is MIT licensed. It uses the NuGet packages below. Licenses are read from each package's nuspec.

## Shipped dependencies

| Package | License |
|---|---|
| System.Text.Json | MIT |
| Microsoft.Extensions.Hosting | MIT |
| Microsoft.Extensions.DependencyInjection | MIT |
| Microsoft.Extensions.Logging.Abstractions | MIT |
| ModelContextProtocol.Core | Apache-2.0 |
| FlaUI.UIA3 | MIT (LICENSE.txt in the package) |
| YamlDotNet | MIT |
| CommunityToolkit.Mvvm | MIT |

Transitive dependencies of these packages keep their own licenses.

## Build-time only (not shipped)

| Package | License | Note |
|---|---|---|
| Nice3point.Revit.Api.RevitAPI, RevitAPIUI, AdWindows | MIT | Community reference assemblies used to compile the Bridge. They are referenced with `ExcludeAssets="runtime"` and `PrivateAssets="all"`, so no Autodesk DLL is shipped. |
| Microsoft.NETFramework.ReferenceAssemblies | MIT | Reference assemblies for the net48 Bridge builds. |

## Test only

| Package | License |
|---|---|
| xunit | Apache-2.0 |
| xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |

Autodesk and Revit are registered trademarks of Autodesk, Inc. RevitAgent is not affiliated with Autodesk.
