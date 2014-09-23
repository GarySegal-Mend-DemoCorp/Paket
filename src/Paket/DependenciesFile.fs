namespace Paket

open System
open System.IO
open Paket
open Paket.Logging

/// [omit]
module DependenciesFileParser = 

    let private basicOperators = ["~>";">=";"="]
    let private operators = basicOperators @ (basicOperators |> List.map (fun o -> "!" + o))

    let parseVersionRange (text : string) : VersionRange = 
        if text = "" || text = null then VersionRange.AtLeast("0") else
        let splitVersion (text:string) =            
            match basicOperators |> List.tryFind(text.StartsWith) with
            | Some token -> token, text.Replace(token + " ", "")
            | None -> "=", text

        try
            match splitVersion text with
            | ">=", version -> VersionRange.AtLeast(version)
            | "~>", minimum ->
                let maximum =                    
                    let promote index (values:string array) =
                        let parsed, number = Int32.TryParse values.[index]
                        if parsed then values.[index] <- (number + 1).ToString()
                        if values.Length > 1 then values.[values.Length - 1] <- "0"
                        values

                    let parts = minimum.Split '.'
                    let penultimateItem = Math.Max(parts.Length - 2, 0)
                    let promoted = parts |> promote penultimateItem
                    String.Join(".", promoted)
                VersionRange.Between(minimum, maximum)
            | _, version -> VersionRange.Exactly(version)
        with
        | _ -> failwithf "could not parse version range \"%s\"" text

    let private (|Remote|Package|Blank|ReferencesMode|SourceFile|) (line:string) =
        match line.Trim() with
        | _ when String.IsNullOrWhiteSpace line -> Blank
        | trimmed when trimmed.StartsWith "source" ->
            let parts = trimmed.Split ' '
            Remote (parts.[1].Replace("\"",""))                
        | trimmed when trimmed.StartsWith "nuget" -> 
            let parts = trimmed.Replace("nuget","").Trim().Replace("\"", "").Split([|' '|],StringSplitOptions.RemoveEmptyEntries) |> Seq.toList
            match parts with
            | name :: operator :: version  :: _ 
                when List.exists ((=) operator) operators -> Package(name,operator + " " + version)
            | name :: version :: _ -> Package(name,version)
            | name :: _ -> Package(name,">= 0")
            | _ -> failwithf "could not retrieve nuget package from %s" trimmed
        | trimmed when trimmed.StartsWith "references" -> ReferencesMode(trimmed.Replace("references","").Trim() = "strict")
        | trimmed when trimmed.StartsWith "github" ->
            let parts = trimmed.Replace("\"", "").Trim().Split([|' '|],StringSplitOptions.RemoveEmptyEntries)
            let getParts (projectSpec:string) =
                match projectSpec.Split [|':'; '/'|] with
                | [| owner; project |] -> owner, project, None
                | [| owner; project; commit |] -> owner, project, Some commit
                | _ -> failwithf "invalid github specification:%s     %s" Environment.NewLine trimmed
            match parts with
            | [| _; projectSpec; fileSpec |] -> SourceFile(getParts projectSpec, fileSpec)
            | _ -> failwithf "invalid github specification:%s     %s" Environment.NewLine trimmed
        | _ -> Blank
    
    let parseDependenciesFile fileName (lines:string seq) = 
        ((0, false, [], [], []), lines)
        ||> Seq.fold(fun (lineNo, referencesMode, sources: PackageSource list, packages, sourceFiles) line ->
            let lineNo = lineNo + 1
            try
                match line with
                | Remote newSource -> lineNo, referencesMode, (PackageSource.Parse(newSource.TrimEnd([|'/'|])) :: sources), packages, sourceFiles
                | Blank -> lineNo, referencesMode, sources, packages, sourceFiles
                | ReferencesMode mode -> lineNo, mode, sources, packages, sourceFiles
                | Package(name,version) ->
                    lineNo, referencesMode, sources, 
                        { Sources = sources
                          Name = name
                          ResolverStrategy = if version.StartsWith "!" then ResolverStrategy.Min else ResolverStrategy.Max
                          VersionRange = parseVersionRange(version.Trim '!') } :: packages, sourceFiles
                | SourceFile((owner,project, commit), path) ->
                    // TODO: Put SHA1 retrieval into resolver
                    let specified,sha = 
                        match commit with                        
                        | None -> false,GitHub.getSHA1OfBranch owner project "master" |> Async.RunSynchronously
                        | Some sha -> true,sha
                    
                    lineNo, referencesMode, sources, packages, { Owner = owner; Project = project; Commit = sha; CommitSpecified = specified; Name = path } :: sourceFiles
                    
            with
            | exn -> failwithf "Error in paket.dependencies line %d%s  %s" lineNo Environment.NewLine exn.Message)
        |> fun (_,mode,_,packages,remoteFiles) ->
            fileName,
            mode,
            packages |> List.rev,
            remoteFiles |> List.rev

module DependenciesFileSerializer = 
    let formatVersionRange (version : VersionRange) : string =
        match version with
        | Minimum x -> ">= " + x.ToString()
        | GreaterThan x -> x.ToString()
        | Specific x -> "= " + x.ToString()
        | VersionRange.Range(fromB,from,_to,_toB) when 
            DependenciesFileParser.parseVersionRange("~> " + from.ToString()) = version
              -> "~> " + from.ToString()

/// Allows to parse and analyze paket.dependencies files.
type DependenciesFile(fileName,strictMode,packages : UnresolvedPackage list, remoteFiles : SourceFile list) = 
    let packages = packages |> Seq.toList
    let dependencyMap = Map.ofSeq (packages |> Seq.map (fun p -> p.Name, p.VersionRange))
    member __.DirectDependencies = dependencyMap
    member __.Packages = packages
    member __.RemoteFiles = remoteFiles
    member __.Strict = strictMode
    member __.FileName = fileName
    member this.Resolve(force) = this.Resolve(Nuget.GetVersions,Nuget.GetPackageDetails force)
    member __.Resolve(getVersionF, getPackageDetailsF) = PackageResolver.Resolve(getVersionF, getPackageDetailsF, packages)
    member __.Add(packageName,version) =
        let lastPackage = Seq.last packages
        let versionRange = DependenciesFileParser.parseVersionRange version
        let newPackage = {lastPackage with Name = packageName; VersionRange = versionRange}
        tracefn "Adding %s %s to paket.dependencies" packageName (versionRange.ToString())
        DependenciesFile(fileName,strictMode,packages @ [newPackage], remoteFiles)

    override __.ToString() =        
        let sources = 
            packages
            |> Seq.map (fun package -> package.Sources,package)
            |> Seq.groupBy fst

        let all =
            let hasReportedSource = ref false
            let hasReportedFirst = ref false
            let hasReportedSecond = ref false
            [ if strictMode then
                  yield "references strict"
              for sources, packages in sources do
                  for source in sources do
                      hasReportedSource := true
                      match source with
                      | Nuget source -> yield "source " + source
                      | LocalNuget source -> yield "source " + source
                  
                  for _,package in packages do
                      if (not !hasReportedFirst) && !hasReportedSource  then
                          yield ""
                          hasReportedFirst := true

                      match package.VersionRange with
                      | Minimum x when x = SemVer.parse "0" -> yield sprintf "nuget %s" package.Name
                      | _ -> yield sprintf "nuget %s %s" package.Name (DependenciesFileSerializer.formatVersionRange package.VersionRange)
                     
              for remoteFile in remoteFiles do
                  if (not !hasReportedSecond) && !hasReportedFirst then
                      yield ""
                      hasReportedSecond := true

                  if remoteFile.CommitSpecified then
                      yield sprintf "github %s/%s:%s %s" remoteFile.Owner remoteFile.Project remoteFile.Commit remoteFile.Name
                  else
                      yield sprintf "github %s/%s %s" remoteFile.Owner remoteFile.Project remoteFile.Name]
        String.Join(Environment.NewLine, all)                                 

    member this.Save() =
        File.WriteAllText(fileName, this.ToString())
        tracefn "Dependencies files saved to %s" fileName

    static member FromCode(code:string) : DependenciesFile = 
        DependenciesFile(DependenciesFileParser.parseDependenciesFile "" <| code.Replace("\r\n","\n").Replace("\r","\n").Split('\n'))
    static member ReadFromFile fileName : DependenciesFile = 
        tracefn "Parsing %s" fileName
        DependenciesFile(DependenciesFileParser.parseDependenciesFile fileName <| File.ReadAllLines fileName)

    /// Find the matching lock file to a dependencies file
    static member FindLockfile(dependenciesFileName) =
        let fi = FileInfo(dependenciesFileName)
        FileInfo(Path.Combine(fi.Directory.FullName, fi.Name.Replace(fi.Extension,"") + ".lock"))

    /// Find the matching lock file to a dependencies file
    member this.FindLockfile() = DependenciesFile.FindLockfile this.FileName