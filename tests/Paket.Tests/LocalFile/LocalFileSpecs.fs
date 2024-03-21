module Paket.LocalFileSpecs

open FsToolkit.ErrorHandling

open NUnit.Framework

open FsUnit

open Paket.Domain
open Paket.PackageSources
open Paket.TestHelpers

[<Test>]
let ``should parse single dev source override``() = 
    let contents = """
        nuget NUnit -> source ./local_source
        """
    let expected = 
        LocalFile [
            LocalSourceOverride
                (LocalFile.nameGroup ("NUnit", "main"), 
                 LocalNuGet ("./local_source", None),
                 None) 
        ]
        |> Ok

    let actual = LocalFile.parse (toLines contents |> Array.toList)

    actual |> shouldEqual expected

[<Test>]
let ``should parse single dev source override in group``() = 
    let contents = """
        nuget NUnit group Build -> source ./local_source
        """
    let expected = 
        LocalFile [
            LocalSourceOverride 
                (LocalFile.nameGroup ("NUnit", "Build"),
                 LocalNuGet ("./local_source", None),
                 None) 
        ]
        |> Ok

    let actual = LocalFile.parse (toLines contents |> Array.toList)

    actual |> shouldEqual expected


[<Test>]
let ``should parse single dev source override with version``() = 
    let contents = """
        nuget NUnit -> source ./local_source version 0.0.0
        """
    let expected = 
        LocalFile [
            LocalSourceOverride 
                (LocalFile.nameGroup ("NUnit", "main"),
                 LocalNuGet ("./local_source", None),
                 Some SemVer.Zero) 
        ]
        |> Ok

    let actual = LocalFile.parse (toLines contents |> Array.toList)

    actual |> shouldEqual expected

[<Test>]
let ``should ignore comments``() = 
    let contents = """
        // override NUnit with nupkg from local directory
        nuget NUnit -> source ./local_source
        # override FAKE with nupkg built from git repository
        nuget FAKE -> git file:\\\c:/github/FAKE fature_branch build:"build.cmd", Packages: /bin/
        """
    
    let actual = LocalFile.parse (toLines contents |> Array.toList)

    match actual with
    | Ok (LocalFile overrides) ->
        overrides |> shouldHaveLength 2
    | Error msgs ->
        Assert.Fail (msgs |> String.concat System.Environment.NewLine)