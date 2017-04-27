namespace Paket.LockFile
open Paket
open NUnit.Framework

[<TestFixture (Category=Category.LockFile)>]
module GenerateAuthModeSpecs =

    open Paket
    open NUnit.Framework
    open FsUnit
    open TestHelpers
    open Paket.Domain

    let config1 = """
source "http://www.nuget.org/api/v2"  username: "user" password: "pass"

nuget "Castle.Windsor-log4net" "~> 3.2" """ |> trimAndNormalizeLines

    let graph =
        OfSimpleGraph [
            "Castle.Windsor-log4net","3.2",[]
        ]

    let expected = """
NUGET
  remote: http://www.nuget.org/api/v2
    Castle.Windsor-log4net (3.2)""" |> trimAndNormalizeLines

    [<Test>]
    let ``should generate no auth in lock file``() = 
        let cfg = DependenciesFile.FromSource(config1)
        ResolveWithGraph(cfg,noSha1,VersionsFromGraphAsSeq graph, PackageDetailsFromGraph graph).[Constants.MainDependencyGroup].ResolvedPackages.GetModelOrFail()
        |> LockFileSerializer.serializePackages cfg.Groups.[Constants.MainDependencyGroup].Options
        |> shouldEqual (normalizeLineEndings expected)