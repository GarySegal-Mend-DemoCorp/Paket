namespace Paket

open System
open System.IO
open Paket
open Paket.Domain
open Paket.Logging
open Paket.Requirements
open Paket.ModuleResolver
open Paket.PackageResolver
open Paket.PackageSources

/// [omit]
type InstallOptions = 
    { Strict : bool 
      Redirects : bool 
      OmitContent : bool }

    static member Default = { Strict = false; OmitContent = false; Redirects = false}

/// [omit]
module DependenciesFileParser = 

    let private basicOperators = ["~>";"==";"<=";">=";"=";">";"<"]
    let private operators = basicOperators @ (basicOperators |> List.map (fun o -> "!" + o))

    let parseResolverStrategy (text : string) = if text.StartsWith "!" then ResolverStrategy.Min else ResolverStrategy.Max

    let twiddle(minimum:string) =                    
        let promote index (values:string array) =
            let parsed, number = Int32.TryParse values.[index]
            if parsed then values.[index] <- (number + 1).ToString()
            if values.Length > 1 then values.[values.Length - 1] <- "0"
            values

        let parts = minimum.Split '.'
        let penultimateItem = Math.Max(parts.Length - 2, 0)
        let promoted = parts |> promote penultimateItem
        String.Join(".", promoted)

    let parseVersionRequirement (text : string) : VersionRequirement =
        let parsePrerelease(texts:string seq) =
            let texts = texts |> Seq.filter ((<>) "")
            if Seq.isEmpty texts then PreReleaseStatus.No else
            if Seq.head(texts).ToLower() = "prerelease" then PreReleaseStatus.All else
            PreReleaseStatus.Concrete(texts |> Seq.toList)

        if text = "" || text = null then VersionRequirement(VersionRange.AtLeast("0"),PreReleaseStatus.No) else

        match text.Split(' ') |> Array.toList with
        |  ">=" :: v1 :: "<" :: v2 :: rest -> VersionRequirement(VersionRange.Range(VersionRangeBound.Including,SemVer.Parse v1,SemVer.Parse v2,VersionRangeBound.Excluding),parsePrerelease rest)
        |  ">=" :: v1 :: "<=" :: v2 :: rest -> VersionRequirement(VersionRange.Range(VersionRangeBound.Including,SemVer.Parse v1,SemVer.Parse v2,VersionRangeBound.Including),parsePrerelease rest)
        |  "~>" :: v1 :: ">=" :: v2 :: rest -> VersionRequirement(VersionRange.Range(VersionRangeBound.Including,SemVer.Parse v2,SemVer.Parse(twiddle v1),VersionRangeBound.Excluding),parsePrerelease rest)
        |  "~>" :: v1 :: ">" :: v2 :: rest -> VersionRequirement(VersionRange.Range(VersionRangeBound.Excluding,SemVer.Parse v2,SemVer.Parse(twiddle v1),VersionRangeBound.Excluding),parsePrerelease rest)
        |  ">" :: v1 :: "<" :: v2 :: rest -> VersionRequirement(VersionRange.Range(VersionRangeBound.Excluding,SemVer.Parse v1,SemVer.Parse v2,VersionRangeBound.Excluding),parsePrerelease rest)
        |  ">" :: v1 :: "<=" :: v2 :: rest -> VersionRequirement(VersionRange.Range(VersionRangeBound.Excluding,SemVer.Parse v1,SemVer.Parse v2,VersionRangeBound.Including),parsePrerelease rest)
        | _ -> 
            let splitVersion (text:string) =            
                match basicOperators |> List.tryFind(text.StartsWith) with
                | Some token -> token, text.Replace(token + " ", "").Split(' ') |> Array.toList
                | None -> "=", text.Split(' ') |> Array.toList

            try
                match splitVersion text with
                | "==", version :: rest -> VersionRequirement(VersionRange.OverrideAll(SemVer.Parse version),parsePrerelease rest)
                | ">=", version :: rest -> VersionRequirement(VersionRange.AtLeast(version),parsePrerelease rest)
                | ">", version :: rest -> VersionRequirement(VersionRange.GreaterThan(SemVer.Parse version),parsePrerelease rest)
                | "<", version :: rest -> VersionRequirement(VersionRange.LessThan(SemVer.Parse version),parsePrerelease rest)
                | "<=", version :: rest -> VersionRequirement(VersionRange.Maximum(SemVer.Parse version),parsePrerelease rest)
                | "~>", minimum :: rest -> VersionRequirement(VersionRange.Between(minimum,twiddle minimum),parsePrerelease rest)
                | _, version :: rest -> VersionRequirement(VersionRange.Exactly(version),parsePrerelease rest)
                | _ -> failwithf "could not parse version range \"%s\"" text
            with
            | _ -> failwithf "could not parse version range \"%s\"" text

    let parseDependencyLine (line:string) =
        let rec parseDepLine start acc =
            if start >= line.Length then acc
            else
                match line.[start] with
                | ' ' -> parseDepLine (start+1) acc
                | '"' ->
                    match line.IndexOf('"', start+1) with
                    | -1  -> failwithf "Unclosed quote in line '%s'" line
                    | ind -> parseDepLine (ind+1) (line.Substring(start+1, ind-start-1)::acc)
                | _ ->
                    match line.IndexOf(' ', start+1) with
                    | -1  -> line.Substring(start)::acc
                    | ind -> parseDepLine (ind+1) (line.Substring(start, ind-start)::acc)
        parseDepLine 0 []
        |> List.rev
        |> List.toArray


    let private ``parse git source`` trimmed origin originTxt = 
        let parts = parseDependencyLine trimmed
        let getParts (projectSpec:string) =
            match projectSpec.Split [|':'; '/'|] with
            | [| owner; project |] -> owner, project, None
            | [| owner; project; commit |] -> owner, project, Some commit
            | _ -> failwithf "invalid %s specification:%s     %s" originTxt Environment.NewLine trimmed
        match parts with
        | [| _; projectSpec; fileSpec |] -> origin, getParts projectSpec, fileSpec
        | [| _; projectSpec;  |] -> origin, getParts projectSpec, Constants.FullProjectSourceFileName
        | _ -> failwithf "invalid %s specification:%s     %s" originTxt Environment.NewLine trimmed

    let private ``parse http source`` trimmed =
        let parts = parseDependencyLine trimmed
        let getParts (projectSpec:string) fileSpec =
            let ``project spec`` =
                match projectSpec.EndsWith("/") with
                | false -> projectSpec
                | true ->  projectSpec.Substring(0, projectSpec.Length-1)
            let splitted = ``project spec``.Split([|':'; '/'|], StringSplitOptions.RemoveEmptyEntries)
            let fileName = match String.IsNullOrEmpty fileSpec with
                            | true ->
                                let name = Seq.last splitted
                                if String.IsNullOrEmpty <| Path.GetExtension(name)
                                then name + ".fs" else name
                            | false -> fileSpec
            match splitted |> Seq.truncate 4 |> Seq.toArray with
            //SourceFile(origin(url), (owner,project, commit), path)
            | [| protocol; domain |] -> HttpLink(``project spec``), (domain, domain, None), fileName
            | [| protocol; domain; project |] -> HttpLink(``project spec``), (domain,project, None), fileName
            | [| protocol; owner; project; details |] -> HttpLink(``project spec``), (owner,project+"/"+details, None), fileName
            | _ -> failwithf "invalid http-reference specification:%s     %s" Environment.NewLine trimmed
        match parts with
        | [| _; projectSpec; |] -> getParts projectSpec String.Empty
        | [| _; projectSpec; fileSpec |] -> getParts projectSpec fileSpec
        | _ -> failwithf "invalid http-reference specification:%s     %s" Environment.NewLine trimmed

    type private ParserOption =
    | ReferencesMode of bool
    | OmitContent of bool
    | Redirects of bool

    let private (|Remote|Package|Blank|ParserOptions|SourceFile|) (line:string) =
        match line.Trim() with
        | _ when String.IsNullOrWhiteSpace line -> Blank
        | String.StartsWith "source" _ as trimmed -> Remote(PackageSource.Parse(trimmed))
        | String.StartsWith "nuget" trimmed -> 
            let parts = trimmed.Trim().Replace("\"", "").Split([|' '|],StringSplitOptions.RemoveEmptyEntries) |> Seq.toList

            let isVersion(text:string) = 
                match Int32.TryParse(text.[0].ToString()) with
                | true,_ -> true
                | _ -> false
           
            match parts with
            | name :: operator1 :: version1  :: operator2 :: version2 :: rest
                when List.exists ((=) operator1) operators && List.exists ((=) operator2) operators -> Package(name,operator1 + " " + version1 + " " + operator2 + " " + version2 + " " + String.Join(" ",rest))
            | name :: operator :: version  :: rest 
                when List.exists ((=) operator) operators -> Package(name,operator + " " + version + " " + String.Join(" ",rest))
            | name :: version :: rest when isVersion version -> 
                Package(name,version + " " + String.Join(" ",rest))
            | name :: rest -> Package(name,">= 0 " + String.Join(" ",rest))
            | name :: [] -> Package(name,">= 0")
            | _ -> failwithf "could not retrieve nuget package from %s" trimmed
        | String.StartsWith "references" trimmed -> ParserOptions(ParserOption.ReferencesMode(trimmed.Trim() = "strict"))
        | String.StartsWith "redirects" trimmed -> ParserOptions(ParserOption.Redirects(trimmed.Trim() = "on"))
        | String.StartsWith "content" trimmed -> ParserOptions(ParserOption.OmitContent(trimmed.Trim() = "none"))
        | String.StartsWith "gist" _ as trimmed ->
            SourceFile(``parse git source`` trimmed SingleSourceFileOrigin.GistLink "gist")
        | String.StartsWith "github" _ as trimmed  ->
            SourceFile(``parse git source`` trimmed SingleSourceFileOrigin.GitHubLink "github")
        | String.StartsWith "http" _ as trimmed  ->
            SourceFile(``parse http source`` trimmed)
        | _ -> Blank

    let parseSourceFileDependency source: UnresolvedSourceFile =
        match source with
        | SourceFile (origin, (owner, project, commit), path) -> { Owner = owner; Project = project; Commit = commit; Name = path; Origin = origin }
        | _ -> failwithf "invalid dependency specification:%s     %s" Environment.NewLine source
    
    let parseDependenciesFile fileName (lines:string seq) = 
        ((0, InstallOptions.Default, [], [], []), lines)
        ||> Seq.fold(fun (lineNo, options, sources: PackageSource list, packages, sourceFiles: UnresolvedSourceFile list) line ->
            let lineNo = lineNo + 1
            try
                match line with
                | Remote(newSource) -> lineNo, options, sources @ [newSource], packages, sourceFiles
                | Blank -> lineNo, options, sources, packages, sourceFiles
                | ParserOptions(ParserOption.ReferencesMode mode) -> lineNo, { options with Strict = mode }, sources, packages, sourceFiles
                | ParserOptions(ParserOption.Redirects mode) -> lineNo, { options with Redirects = mode }, sources, packages, sourceFiles
                | ParserOptions(ParserOption.OmitContent omit) -> lineNo, { options with OmitContent = omit }, sources, packages, sourceFiles
                | Package(name,version) ->
                    lineNo, options, sources, 
                        { Sources = sources
                          Name = PackageName name
                          ResolverStrategy = parseResolverStrategy version
                          Parent = DependenciesFile fileName
                          FrameworkRestrictions = []
                          VersionRequirement = parseVersionRequirement(version.Trim '!') } :: packages, sourceFiles
                | SourceFile(origin, (owner,project, commit), path) ->
                    lineNo, options, sources, packages, { Owner = owner; Project = project; Commit = commit; Name = path; Origin = origin} :: sourceFiles
                    
            with
            | exn -> failwithf "Error in paket.dependencies line %d%s  %s" lineNo Environment.NewLine exn.Message)
        |> fun (_,options,sources,packages,remoteFiles) ->
            fileName,
            options,
            sources,
            packages |> List.rev,
            remoteFiles |> List.rev

module DependenciesFileSerializer = 
    let formatVersionRange strategy (version : VersionRequirement) : string =          
        let prefix = 
            if strategy = ResolverStrategy.Min then "!"
            else ""

        let preReleases = 
            match version.PreReleases with
            | No -> ""
            | PreReleaseStatus.All -> "prerelease"
            | Concrete list -> String.Join(" ",list)
            
        let version = 
            match version.Range with
            | Minimum x when strategy = ResolverStrategy.Max && x = SemVer.Parse "0" -> ""
            | Minimum x -> ">= " + x.ToString()
            | GreaterThan x -> "> " + x.ToString()
            | Specific x -> x.ToString()
            | VersionRange.Range(_, from, _, _) 
                    when DependenciesFileParser.parseVersionRequirement ("~> " + from.ToString() + preReleases) = version -> 
                        "~> " + from.ToString()
            | _ -> version.ToString()
            
        let text = prefix + version         
        if text <> "" && preReleases <> "" then text + " " + preReleases else text + preReleases

/// Allows to parse and analyze paket.dependencies files.
type DependenciesFile(fileName,options,sources,packages : PackageRequirement list, remoteFiles : UnresolvedSourceFile list) = 
    let packages = packages |> Seq.toList
    let dependencyMap = Map.ofSeq (packages |> Seq.map (fun p -> p.Name, p.VersionRequirement))
            
    member __.DirectDependencies = dependencyMap
    member __.Packages = packages
    member __.HasPackage (name : PackageName) = packages |> List.exists (fun p -> NormalizedPackageName p.Name = NormalizedPackageName name)
    member __.RemoteFiles = remoteFiles
    member __.HasRemoteFile name = remoteFiles |> List.exists (fun r -> r.Name = name)
    member __.Options = options
    member __.FileName = fileName
    member __.Sources = sources
    member this.Resolve(force) = 
        let getSha1 origin owner repo branch = RemoteDownload.getSHA1OfBranch origin owner repo branch |> Async.RunSynchronously
        this.Resolve(getSha1,NuGetV2.GetVersions,NuGetV2.GetPackageDetails force)

    member __.Resolve(getSha1,getVersionF, getPackageDetailsF) =
        let resolveSourceFile(file:ResolvedSourceFile) : PackageRequirement list =
            RemoteDownload.downloadDependenciesFile(Path.GetDirectoryName fileName, file)
            |> Async.RunSynchronously
            |> DependenciesFile.FromCode
            |> fun df -> df.Packages

        let remoteFiles = ModuleResolver.Resolve(resolveSourceFile,getSha1,remoteFiles)
        
        let remoteDependencies = 
            remoteFiles
            |> List.map (fun f -> f.Dependencies)
            |> List.fold (fun set current -> Set.union set current) Set.empty
            |> Seq.map (fun (n, v) -> 
                   let p = packages |> Seq.last
                   { p with Name = n
                            VersionRequirement = v })
            |> Seq.toList

        { ResolvedPackages = PackageResolver.Resolve(getVersionF, getPackageDetailsF, remoteDependencies @ packages)
          ResolvedSourceFiles = remoteFiles }        

    member __.AddAdditionalRemote(origin, url, remoteFileName) =
        let sources = 
            match packages |> List.rev with
            | lastPackage::_ -> lastPackage.Sources
            | [] -> [PackageSources.DefaultNugetSource]
      
        let newRemoteFile = sprintf "%s %s %s" origin url remoteFileName |> DependenciesFileParser.parseSourceFileDependency
         
        DependenciesFile(fileName, options, sources, packages, remoteFiles @ [newRemoteFile])

    member __.AddAdditionionalPackage(packageName:PackageName,version:string) =
        let versionRange = DependenciesFileParser.parseVersionRequirement (version.Trim '!')
        let sources = 
            match packages |> List.rev with
            | lastPackage::_ -> lastPackage.Sources
            | [] -> [PackageSources.DefaultNugetSource]

        let newPackage = 
            { Name = packageName
              VersionRequirement = versionRange
              Sources = sources
              ResolverStrategy = DependenciesFileParser.parseResolverStrategy version
              FrameworkRestrictions = []
              Parent = PackageRequirementSource.DependenciesFile fileName }

        DependenciesFile(fileName,options,sources,packages @ [newPackage], remoteFiles)

    member __.AddFixedPackage(packageName:PackageName,version:string,frameworkRestrictions) =
        let versionRange = DependenciesFileParser.parseVersionRequirement (version.Trim '!')
        let sources = 
            match packages |> List.rev with
            | lastPackage::_ -> lastPackage.Sources
            | [] -> [PackageSources.DefaultNugetSource]

        let strategy,newVersionRange = 
            match packages |> List.tryFind (fun p -> NormalizedPackageName p.Name = NormalizedPackageName packageName) with
            | Some package -> 
                package.ResolverStrategy,
                match package.VersionRequirement.Range with
                | OverrideAll(_) -> package.VersionRequirement
                | _ -> versionRange
            | None -> DependenciesFileParser.parseResolverStrategy version,versionRange

        let newPackage = 
            { Name = packageName
              VersionRequirement = newVersionRange
              Sources = sources
              ResolverStrategy = strategy
              FrameworkRestrictions = frameworkRestrictions
              Parent = PackageRequirementSource.DependenciesFile fileName }

        DependenciesFile(fileName,options,sources,(packages |> List.filter (fun p -> NormalizedPackageName p.Name <> NormalizedPackageName packageName)) @ [newPackage], remoteFiles)

    member this.AddFixedPackage(packageName:PackageName,version:string) =
        this.AddFixedPackage(packageName,version,[])

    member __.RemovePackage(packageName:PackageName) =
        let newPackages = 
            packages
            |> List.filter (fun p -> NormalizedPackageName p.Name <> NormalizedPackageName packageName)

        DependenciesFile(fileName,options,sources,newPackages,remoteFiles)

    member this.Add(packageName,version:string) =
        let (PackageName name) = packageName
        if this.HasPackage packageName then 
            traceWarnfn "%s contains package %s already. ==> Ignored" fileName name
            this
        else
            if version = "" then
                tracefn "Adding %s to %s" name fileName
            else
                tracefn "Adding %s %s to %s" name version fileName
            this.AddAdditionionalPackage(packageName,version)

    member this.AddRemote(origin, url, remoteFileName) =
        if this.HasRemoteFile remoteFileName then
            traceWarnfn "%s contains file %s already. ==> Ignored" fileName remoteFileName
            this
        else
            tracefn "Adding %s to %s" remoteFileName fileName
            this.AddAdditionalRemote(origin, url, remoteFileName)

    member this.Remove(packageName) =
        let (PackageName name) = packageName
        if this.HasPackage packageName then         
            tracefn "Removing %s from %s" name fileName
            this.RemovePackage(packageName)
        else
            traceWarnfn "%s doesn't contain package %s. ==> Ignored" fileName name
            this

    member this.UpdatePackageVersion(packageName, version) =
        let (PackageName name) = packageName
        if this.HasPackage(packageName) then
            let versionRequirement = DependenciesFileParser.parseVersionRequirement version
            tracefn "Updating %s version to %s in %s" name version fileName
            let packages = 
                this.Packages |> List.map (fun p -> 
                                     if NormalizedPackageName p.Name = NormalizedPackageName packageName then 
                                         { p with VersionRequirement = versionRequirement }
                                     else p)
            DependenciesFile(this.FileName, this.Options, sources, packages, this.RemoteFiles)
        else
            traceWarnfn "%s doesn't contain package %s. ==> Ignored" fileName name
            this

    member this.GetAllPackageSources() = 
        this.Packages
        |> List.collect (fun package -> package.Sources)
        |> Seq.distinct
        |> Seq.toList

    override __.ToString() =        
        let sources = 
            packages
            |> Seq.map (fun package -> package.Sources,package)
            |> Seq.groupBy fst

        let formatNugetSource source = 
            "source " + source.Url +
                match source.Authentication with
                | Some (PlainTextAuthentication(username,password)) -> 
                    sprintf " username: \"%s\" password: \"%s\"" username password
                | Some (EnvVarAuthentication(usernameVar,passwordVar)) -> 
                    sprintf " username: \"%s\" password: \"%s\"" usernameVar.Variable passwordVar.Variable
                | _ -> ""
                 
        let all =
            let hasReportedSource = ref false
            let hasReportedFirst = ref false
            let hasReportedSecond = ref false
            [ if options.Strict then yield "references strict"
              if options.OmitContent then yield "content none"
              for sources, packages in sources do
                  for source in sources do
                      hasReportedSource := true
                      match source with
                      | Nuget source -> yield formatNugetSource source
                      | LocalNuget source -> yield "source " + source
                  
                  for _,package in packages do
                      if (not !hasReportedFirst) && !hasReportedSource  then
                          yield ""
                          hasReportedFirst := true

                      let (PackageName name) = package.Name
                      let version = DependenciesFileSerializer.formatVersionRange package.ResolverStrategy package.VersionRequirement
                      yield sprintf "nuget %s%s" name (if version <> "" then " " + version else "")
                     
              for remoteFile in remoteFiles do
                  if (not !hasReportedSecond) && !hasReportedFirst then
                      yield ""
                      hasReportedSecond := true
                      
                  yield remoteFile.ToString() ]
                  
                  
        String.Join(Environment.NewLine, all)                                 

    member this.Save() =
        File.WriteAllText(fileName, this.ToString())
        tracefn "Dependencies files saved to %s" fileName

    static member FromCode(code:string) : DependenciesFile = 
        DependenciesFile(DependenciesFileParser.parseDependenciesFile "" <| code.Replace("\r\n","\n").Replace("\r","\n").Split('\n'))

    static member ReadFromFile fileName : DependenciesFile = 
        verbosefn "Parsing %s" fileName
        DependenciesFile(DependenciesFileParser.parseDependenciesFile fileName <| File.ReadAllLines fileName)

    /// Find the matching lock file to a dependencies file
    static member FindLockfile(dependenciesFileName) =
        let fi = FileInfo(dependenciesFileName)
        FileInfo(Path.Combine(fi.Directory.FullName, fi.Name.Replace(fi.Extension,"") + ".lock"))

    /// Find the matching lock file to a dependencies file
    member this.FindLockfile() = DependenciesFile.FindLockfile this.FileName