namespace Paket

open System
open System.IO
open Paket.Logging
open Paket.PackageResolver
open Paket.ModuleResolver
open Paket.PackageSources

module LockFileSerializer =
    /// [omit]
    let formatVersionRange (version : VersionRequirement) = 
        match version.Range with
        | Minimum v -> ">= " + v.ToString()
        | Specific v -> v.ToString()
        | Range(_, v1, v2, _) -> ">= " + v1.ToString() + ", < " + v2.ToString()

    /// [omit]
    let serializePackages options (resolved : PackageResolution) = 
        let sources = 
            resolved
            |> Seq.map (fun kv ->
                    let package = kv.Value
                    match package.Source with
                    | Nuget source -> source.Url,source.Auth,package
                    | LocalNuget path -> path,None,package
                )
            |> Seq.groupBy (fun (a,b,_) -> a,b)

        let all = 
            let hasReported = ref false
            [ if options.Strict then yield "REFERENCES: STRICT"
              if options.OmitContent then yield "CONTENT: NONE"
              for (source, _), packages in sources do
                  if not !hasReported then
                    yield "NUGET"
                    hasReported := true

                  yield "  remote: " + source

                  yield "  specs:"
                  for _,_,package in packages |> Seq.sortBy (fun (_,_,p) -> p.Name.ToLower()) do
                      yield sprintf "    %s (%s)" package.Name (package.Version.ToString()) 
                      for name,v in package.Dependencies do
                          yield sprintf "      %s (%s)" name (formatVersionRange v)]
    
        String.Join(Environment.NewLine, all)

    let serializeSourceFiles (files:ResolvedSourceFile list) =    
        let all =
            let hasReported = ref false
            [ for (owner,project), files in files |> Seq.groupBy(fun f -> f.Owner, f.Project) do
                if not !hasReported then
                    yield "GITHUB"
                    hasReported := true

                yield sprintf "  remote: %s/%s" owner project
                yield "  specs:"
                for file in files |> Seq.sortBy (fun f -> f.Owner.ToLower(),f.Project.ToLower(),f.Name.ToLower())  do
                    let path = file.Name.TrimStart '/'
                    yield sprintf "    %s (%s)" path file.Commit 
                    for dep in file.Dependencies do
                        yield sprintf "      %s (%s)" dep.Name (formatVersionRange dep.VersionRequirement)]

        String.Join(Environment.NewLine, all)

module LockFileParser =
    type ParseState =
        { RepositoryType : string option
          RemoteUrl :string option
          Packages : ResolvedPackage list
          SourceFiles : ResolvedSourceFile list
          LastWasPackage : bool
          Options: InstallOptions }
    
    type private InstallOptionCase = StrictCase | OmitContentCase

    let private (|Remote|NugetPackage|NugetDependency|SourceFile|RepositoryType|Blank|InstallOption|) (state, line:string) =
        match (state.RepositoryType, line.Trim()) with
        | _, "NUGET" -> RepositoryType "NUGET"
        | _, "GITHUB" -> RepositoryType "GITHUB"
        | _, _ when String.IsNullOrWhiteSpace line -> Blank
        | _, trimmed when trimmed.StartsWith "remote:" -> Remote(trimmed.Substring(trimmed.IndexOf(": ") + 2).Split(' ').[0])
        | _, trimmed when trimmed.StartsWith "specs:" -> Blank
        | _, trimmed when trimmed.StartsWith "REFERENCES:" -> InstallOption(StrictCase,trimmed.Replace("REFERENCES:","").Trim() = "STRICT")
        | _, trimmed when trimmed.StartsWith "CONTENT:" -> InstallOption(OmitContentCase,trimmed.Replace("CONTENT:","").Trim() = "NONE")
        | _, trimmed when line.StartsWith "      " ->
            let parts = trimmed.Split '(' 
            NugetDependency (parts.[0].Trim(),parts.[1].Replace("(", "").Replace(")", "").Trim())
        | Some "NUGET", trimmed -> NugetPackage trimmed
        | Some "GITHUB", trimmed -> SourceFile trimmed
        | Some _, _ -> failwith "unknown Repository Type."
        | _ -> failwith "unknown lock file format."

    let Parse(lockFileLines) =
        let remove textToRemove (source:string) = source.Replace(textToRemove, "")
        let removeBrackets = remove "(" >> remove ")"
        ({ RepositoryType = None; RemoteUrl = None; Packages = []; SourceFiles = []; Options = InstallOptions.Default; LastWasPackage = false }, lockFileLines)
        ||> Seq.fold(fun state line ->
            match (state, line) with
            | Remote(url) -> { state with RemoteUrl = Some url }
            | Blank -> state
            | InstallOption (StrictCase,mode) -> { state with Options = {state.Options with Strict = mode} }
            | InstallOption (OmitContentCase,omit) -> { state with Options = {state.Options with OmitContent = omit} }
            | RepositoryType repoType -> { state with RepositoryType = Some repoType }
            | NugetPackage details ->
                match state.RemoteUrl with
                | Some remote -> 
                    let parts = details.Split ' '
                    let version = parts.[1] |> removeBrackets
                    { state with LastWasPackage = true
                                 Packages = 
                                     { Source = PackageSource.Parse(remote, None)
                                       Name = parts.[0]
                                       Dependencies = Set.empty
                                       Version = SemVer.parse version } :: state.Packages }
                | None -> failwith "no source has been specified."
            | NugetDependency (name, _) ->
                match state.Packages with
                | currentPackage :: otherPackages -> 
                    if not state.LastWasPackage then state else
                    { state with
                        Packages = { currentPackage with
                                        Dependencies = Set.add (name, VersionRequirement.AllReleases) currentPackage.Dependencies
                                    } :: otherPackages }
                | [] -> failwith "cannot set a dependency - no package has been specified."
            | SourceFile details ->
                match state.RemoteUrl |> Option.map(fun s -> s.Split '/') with
                | Some [| owner; project |] ->
                    let path, commit = match details.Split ' ' with
                                        | [| filePath; commit |] -> filePath, commit |> removeBrackets                                       
                                        | _ -> failwith "invalid file source details."
                    { state with  
                        LastWasPackage = false                      
                        SourceFiles = { Commit = commit
                                        Owner = owner
                                        Project = project
                                        Dependencies = []
                                        Name = path } :: state.SourceFiles }
                | _ -> failwith "invalid remote details.")


/// Allows to parse and analyze paket.lock files.
type LockFile(fileName:string,options,resolution:PackageResolution,remoteFiles:ResolvedSourceFile list) =
    member __.SourceFiles = remoteFiles
    member __.ResolvedPackages = resolution
    member __.FileName = fileName
    member __.Options = options

    /// Updates the Lock file with the analyzed dependencies from the paket.dependencies file.
    member __.Save() =
        let output = 
            String.Join
                (Environment.NewLine,                  
                    LockFileSerializer.serializePackages options resolution, 
                    LockFileSerializer.serializeSourceFiles remoteFiles)
        File.WriteAllText(fileName, output)
        tracefn "Locked version resolutions written to %s" fileName

    /// Parses a paket.lock file from lines
    static member LoadFrom(lockFileName) : LockFile =        
        LockFileParser.Parse(File.ReadAllLines lockFileName)
        |> fun state -> LockFile(lockFileName, state.Options ,state.Packages |> Seq.fold (fun map p -> Map.add p.Name p map) Map.empty, List.rev state.SourceFiles)