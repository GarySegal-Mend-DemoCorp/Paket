﻿namespace System
open System.Reflection

[<assembly: AssemblyTitleAttribute("Paket")>]
[<assembly: AssemblyProductAttribute("Paket")>]
[<assembly: AssemblyDescriptionAttribute("A package dependency manager for .NET with support for NuGet packages and GitHub repositories.")>]
[<assembly: AssemblyVersionAttribute("0.21.5")>]
[<assembly: AssemblyFileVersionAttribute("0.21.5")>]
do ()

module internal AssemblyVersionInformation =
    let [<Literal>] Version = "0.21.5"
