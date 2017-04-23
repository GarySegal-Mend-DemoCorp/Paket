﻿module Paket.RestrictionFilterSpecs

open System.IO
open Paket
open Paket.Domain
open Chessie.ErrorHandling
open FsUnit
open NUnit.Framework
open TestHelpers
open Paket.Requirements

[<Test>]
let ``should filter net45 and >= net40``() = 
    let l1 = FrameworkRestrictionList [FrameworkRestriction.AtLeast(DotNetFramework(FrameworkVersion.V4))]
    let l2 = FrameworkRestrictionList [FrameworkRestriction.Exactly(DotNetFramework(FrameworkVersion.V4_5))]
    filterRestrictions l1 l2
    |> shouldEqual l2

[<Test>]
let ``should filter >= net40 and net45``() = 
    let l1 = FrameworkRestrictionList [FrameworkRestriction.Exactly(DotNetFramework(FrameworkVersion.V4_5))]
    let l2 = FrameworkRestrictionList [FrameworkRestriction.AtLeast(DotNetFramework(FrameworkVersion.V4))]
    filterRestrictions l1 l2
    |> shouldEqual l1


[<Test>]
let ``should filter >= net40 and net40``() = 
    let l1 = FrameworkRestrictionList [FrameworkRestriction.Exactly(DotNetFramework(FrameworkVersion.V4_Client))]
    let l2 = FrameworkRestrictionList [FrameworkRestriction.AtLeast(DotNetFramework(FrameworkVersion.V4_Client))]
    filterRestrictions l1 l2
    |> shouldEqual l1

[<Test>]
let ``should filter >=net40 and >= net45``() = 
    let l1 = FrameworkRestrictionList [FrameworkRestriction.AtLeast(DotNetFramework(FrameworkVersion.V4))]
    let l2 = FrameworkRestrictionList [FrameworkRestriction.AtLeast(DotNetFramework(FrameworkVersion.V4_5))]
    filterRestrictions l1 l2
    |> shouldEqual l2

[<Test>]
let ``should filter >= net40 and portable``() =
    let l1 = FrameworkRestrictionList [FrameworkRestriction.AtLeast(DotNetFramework(FrameworkVersion.V4))]
    let l2 = FrameworkRestrictionList [FrameworkRestriction.Portable("abc")]

    filterRestrictions l1 l2
    |> shouldEqual (FrameworkRestrictionList [])

[<Test>]
let ``should filter >= net40 and >= net20 < net46``() = 
    let l1 = FrameworkRestrictionList [FrameworkRestriction.AtLeast(DotNetFramework(FrameworkVersion.V4))]
    let l2 = FrameworkRestrictionList [FrameworkRestriction.Between(DotNetFramework(FrameworkVersion.V2),DotNetFramework(FrameworkVersion.V4_6))]

    filterRestrictions l1 l2
    |> shouldEqual (FrameworkRestrictionList [FrameworkRestriction.Between(DotNetFramework(FrameworkVersion.V4),DotNetFramework(FrameworkVersion.V4_6))])

[<Test>]
let ``should filter >= net40 and >= net45 < net46``() =
    let l1 = FrameworkRestrictionList [FrameworkRestriction.AtLeast(DotNetFramework(FrameworkVersion.V4))]
    let l2 = FrameworkRestrictionList [FrameworkRestriction.Between(DotNetFramework(FrameworkVersion.V4_5),DotNetFramework(FrameworkVersion.V4_6))]

    filterRestrictions l1 l2
    |> shouldEqual l2

[<Test>]
let ``should filter >= net40 and >= net20 < net40``() = 
    let l1 = FrameworkRestrictionList [FrameworkRestriction.AtLeast(DotNetFramework(FrameworkVersion.V4))]
    let l2 = FrameworkRestrictionList [FrameworkRestriction.Between(DotNetFramework(FrameworkVersion.V2),DotNetFramework(FrameworkVersion.V4))]

    filterRestrictions l1 l2
    |> shouldEqual (FrameworkRestrictionList [])

[<Test>]
let ``should filter net45 and >= net40 < net46``() = 
    let l1 = FrameworkRestrictionList [FrameworkRestriction.Exactly(DotNetFramework(FrameworkVersion.V4_5))]
    let l2 = FrameworkRestrictionList [FrameworkRestriction.Between(DotNetFramework(FrameworkVersion.V4),DotNetFramework(FrameworkVersion.V4_6))]
    filterRestrictions l1 l2
    |> shouldEqual l1

[<Test>]
let ``should filter net45 and >= net40 < net45``() = 
    let l1 = FrameworkRestrictionList [FrameworkRestriction.Exactly(DotNetFramework(FrameworkVersion.V4_5))]
    let l2 = FrameworkRestrictionList [FrameworkRestriction.Between(DotNetFramework(FrameworkVersion.V4),DotNetFramework(FrameworkVersion.V4_5))]
    filterRestrictions l1 l2
    |> shouldEqual (FrameworkRestrictionList [])

[<Test>]
let ``should filter >= net40 < net46 and net45``() = 
    let l1 = FrameworkRestrictionList [FrameworkRestriction.Between(DotNetFramework(FrameworkVersion.V4),DotNetFramework(FrameworkVersion.V4_6))]
    let l2 = FrameworkRestrictionList [FrameworkRestriction.Exactly(DotNetFramework(FrameworkVersion.V4_5))]
    filterRestrictions l1 l2
    |> shouldEqual l2

[<Test>]
let ``should filter >= net40 < net45 and net45``() = 
    let l1 = FrameworkRestrictionList [FrameworkRestriction.Between(DotNetFramework(FrameworkVersion.V4),DotNetFramework(FrameworkVersion.V4_5))]
    let l2 = FrameworkRestrictionList [FrameworkRestriction.Exactly(DotNetFramework(FrameworkVersion.V4_5))]
    filterRestrictions l1 l2
    |> shouldEqual (FrameworkRestrictionList [])

[<Test>]
let ``should filter >= net20 < net46 and >= net40``() = 
    let l1 = FrameworkRestrictionList [FrameworkRestriction.Between(DotNetFramework(FrameworkVersion.V2),DotNetFramework(FrameworkVersion.V4_6))]
    let l2 = FrameworkRestrictionList [FrameworkRestriction.AtLeast(DotNetFramework(FrameworkVersion.V4))]

    filterRestrictions l1 l2
    |> shouldEqual (FrameworkRestrictionList [FrameworkRestriction.Between(DotNetFramework(FrameworkVersion.V4),DotNetFramework(FrameworkVersion.V4_6))])

[<Test>]
let ``should filter >= net45 < net46 and >= net40``() =
    let l1 = FrameworkRestrictionList [FrameworkRestriction.Between(DotNetFramework(FrameworkVersion.V4_5),DotNetFramework(FrameworkVersion.V4_6))]
    let l2 = FrameworkRestrictionList [FrameworkRestriction.AtLeast(DotNetFramework(FrameworkVersion.V4))]

    filterRestrictions l1 l2
    |> shouldEqual l1

[<Test>]
let ``should filter >= net20 < net40 and >= net40``() =
    let l1 = FrameworkRestrictionList [FrameworkRestriction.Between(DotNetFramework(FrameworkVersion.V2),DotNetFramework(FrameworkVersion.V4))]
    let l2 = FrameworkRestrictionList [FrameworkRestriction.AtLeast(DotNetFramework(FrameworkVersion.V4))]

    filterRestrictions l1 l2
    |> shouldEqual (FrameworkRestrictionList [])

[<Test>]
let ``should not filter native, net452``() =
    FrameworkRestriction.Exactly(Native(NoBuildMode,NoPlatform)).IsSameCategoryAs(FrameworkRestriction.Exactly(Native(NoBuildMode,NoPlatform))) |> shouldEqual (Some true)

    let l1 = FrameworkRestrictionList [FrameworkRestriction.Exactly(DotNetFramework(FrameworkVersion.V4_5_2)); FrameworkRestriction.Exactly(Native(NoBuildMode,NoPlatform))]
    let l2 = FrameworkRestrictionList [FrameworkRestriction.Exactly(Native(NoBuildMode,NoPlatform))]

    filterRestrictions l1 l2
    |> shouldEqual (FrameworkRestrictionList [FrameworkRestriction.Exactly(Native(NoBuildMode,NoPlatform))])
