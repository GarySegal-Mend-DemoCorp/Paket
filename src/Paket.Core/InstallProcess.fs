﻿/// Contains methods for the install process.
module Paket.InstallProcess

open Paket
open Chessie.ErrorHandling
open Paket.Domain
open Paket.Logging
open Paket.BindingRedirects
open Paket.ModuleResolver
open Paket.PackageResolver
open System.IO
open System.Collections.Generic
open FSharp.Polyfill
open System.Reflection
open System.Diagnostics
open Paket.Requirements
open System.Security.AccessControl

let findPackageFolder root (PackageName name) =
    let lowerName = name.ToLower()
    let di = DirectoryInfo(Path.Combine(root, Constants.PackagesFolderName))
    let direct = DirectoryInfo(Path.Combine(di.FullName, name))
    if direct.Exists then direct else
    match di.GetDirectories() |> Seq.tryFind (fun subDir -> subDir.FullName.ToLower().EndsWith(lowerName)) with
    | Some x -> x
    | None -> failwithf "Package directory for package %s was not found." name

let private findPackagesWithContent (root,usedPackages:Map<PackageName,InstallSettings>) =
    usedPackages
    |> Seq.filter (fun kv -> defaultArg kv.Value.OmitContent false |> not)
    |> Seq.map (fun kv -> findPackageFolder root kv.Key)
    |> Seq.choose (fun packageDir ->
            packageDir.GetDirectories("Content")
            |> Array.append (packageDir.GetDirectories("content"))
            |> Array.tryFind (fun _ -> true))
    |> Seq.toList

let private copyContentFiles (project : ProjectFile, packagesWithContent) =

    let rules : list<(FileInfo -> bool)> = [
            fun f -> f.Name = "_._"
            fun f -> f.Name.EndsWith(".transform")
            fun f -> f.Name.EndsWith(".pp")
            fun f -> f.Name.EndsWith(".tt")
            fun f -> f.Name.EndsWith(".ttinclude")
        ]

    let onBlackList (fi : FileInfo) = rules |> List.exists (fun rule -> rule(fi))

    let rec copyDirContents (fromDir : DirectoryInfo, toDir : Lazy<DirectoryInfo>) =
        fromDir.GetDirectories() |> Array.toList
        |> List.collect (fun subDir -> copyDirContents(subDir, lazy toDir.Force().CreateSubdirectory(subDir.Name)))
        |> List.append
            (fromDir.GetFiles()
                |> Array.toList
                |> List.filter (onBlackList >> not)
                |> List.map (fun file -> file.CopyTo(Path.Combine(toDir.Force().FullName, file.Name), true)))

    packagesWithContent
    |> List.collect (fun packageDir -> copyDirContents (packageDir, lazy (DirectoryInfo(Path.GetDirectoryName(project.FileName)))))

let private removeCopiedFiles (project: ProjectFile) =
    let rec removeEmptyDirHierarchy (dir : DirectoryInfo) =
        if dir.Exists && dir.EnumerateFileSystemInfos() |> Seq.isEmpty then
            dir.Delete()
            removeEmptyDirHierarchy dir.Parent

    let removeFilesAndTrimDirs (files: FileInfo list) =
        for f in files do
            if f.Exists then
                f.Delete()

        let dirsPathsDeepestFirst =
            files
            |> Seq.map (fun f -> f.Directory.FullName)
            |> Seq.distinct
            |> List.ofSeq
            |> List.rev

        for dirPath in dirsPathsDeepestFirst do
            removeEmptyDirHierarchy (DirectoryInfo dirPath)

    project.GetPaketFileItems()
    |> List.filter (fun fi -> not <| fi.FullName.Contains(Constants.PaketFilesFolderName))
    |> removeFilesAndTrimDirs

let CreateInstallModel(root, sources, force, package) =
    async {
        let! (package, files, targetsFiles) = RestoreProcess.ExtractPackage(root, sources, force, package)
        let (PackageName name) = package.Name
        let nuspec = Nuspec.Load(root,package.Name)
        let files = files |> Array.map (fun fi -> fi.FullName)
        let targetsFiles = targetsFiles |> Array.map (fun fi -> fi.FullName)
        return package, InstallModel.CreateFromLibs(package.Name, package.Version, package.Settings.FrameworkRestrictions, files, targetsFiles, nuspec)
    }

/// Restores the given packages from the lock file.
let createModel(root, sources, force, lockFile : LockFile, packages:Set<NormalizedPackageName>) =
    let sourceFileDownloads = RemoteDownload.DownloadSourceFiles(root, lockFile.SourceFiles)

    let packageDownloads =
        lockFile.ResolvedPackages
        |> Map.filter (fun name _ -> packages.Contains name)
        |> Seq.map (fun kv -> CreateInstallModel(root,sources,force,kv.Value))
        |> Async.Parallel

    let _,extractedPackages =
        Async.Parallel(sourceFileDownloads,packageDownloads)
        |> Async.RunSynchronously

    extractedPackages

/// Applies binding redirects for all strong-named references to all app. and web. config files.
let private applyBindingRedirects root extractedPackages =
    extractedPackages
    |> Seq.map(fun (package, model:InstallModel) -> model.GetLibReferencesLazy.Force())
    |> Set.unionMany
    |> Seq.choose(function | Reference.Library path -> Some path | _-> None)
    |> Seq.groupBy (fun p -> FileInfo(p).Name)
    |> Seq.choose(fun (_,librariesForPackage) ->
        librariesForPackage
        |> Seq.choose(fun library ->
            try
                let assembly = Assembly.ReflectionOnlyLoadFrom library
                assembly
                |> BindingRedirects.getPublicKeyToken
                |> Option.map(fun token -> assembly, token)
            with exn -> None)
        |> Seq.sortBy(fun (assembly,_) -> assembly.GetName().Version)
        |> Seq.toList
        |> List.rev
        |> function | head :: _ -> Some head | _ -> None)
    |> Seq.map(fun (assembly, token) ->
        {   BindingRedirect.AssemblyName = assembly.GetName().Name
            Version = assembly.GetName().Version.ToString()
            PublicKeyToken = token
            Culture = None })
    |> applyBindingRedirectsToFolder root

let findAllReferencesFiles root =
    root
    |> ProjectFile.FindAllProjects
    |> Array.choose (fun p -> ProjectFile.FindReferencesFile(FileInfo(p.FileName))
                                |> Option.map (fun r -> p, r))
    |> Array.map (fun (project,file) ->
        try
            ok <| (project, ReferencesFile.FromFile(file))
        with _ ->
            fail <| ReferencesFileParseError (FileInfo(file)))
    |> collect

/// Installs all packages from the lock file.
let InstallIntoProjects(sources, options : InstallerOptions, lockFile : LockFile, projects : (ProjectFile * ReferencesFile) list) =
    let packagesToInstall =
        if options.OnlyReferenced then
            projects
            |> Seq.ofList
            |> Seq.map (fun (_, referencesFile)->
                referencesFile
                |> lockFile.GetPackageHull
                |> Seq.map (fun p -> NormalizedPackageName p.Key))
            |> Seq.concat
        else
            lockFile.ResolvedPackages
            |> Seq.map (fun kv -> kv.Key)

    let root = Path.GetDirectoryName lockFile.FileName
    let extractedPackages = createModel(root, sources, options.Force, lockFile, Set.ofSeq packagesToInstall)
    let lookup = lockFile.GetDependencyLookupTable()

    let model =
        extractedPackages
        |> Array.map (fun (p,m) -> NormalizedPackageName p.Name,m)
        |> Map.ofArray

    let packages =
        extractedPackages
        |> Array.map (fun (p,m) -> NormalizedPackageName p.Name,p)
        |> Map.ofArray

    for project : ProjectFile, referenceFile in projects do
        verbosefn "Installing to %s" project.FileName
        
        let usedPackages =
            referenceFile.NugetPackages
            |> Seq.map (fun ps ->
                let name = NormalizedPackageName ps.Name
                match packages |> Map.tryFind name with
                 | None -> 
                    failwithf "could not resolve %A (normalized: %A)" ps.Name name
                 | Some package -> 
                    ps.Name,
                        { ps.Settings with
                            FrameworkRestrictions =
                                // TODO: This should filter
                                ps.Settings.FrameworkRestrictions @
                                    lockFile.Options.Settings.FrameworkRestrictions @
                                    package.Settings.FrameworkRestrictions

                            ImportTargets =
                                ps.Settings.ImportTargets ++
                                    package.Settings.ImportTargets ++ 
                                    lockFile.Options.Settings.ImportTargets

                            CopyLocal =
                                ps.Settings.CopyLocal ++ 
                                    package.Settings.CopyLocal ++
                                    lockFile.Options.Settings.CopyLocal

                            OmitContent =
                                ps.Settings.OmitContent ++ 
                                    package.Settings.OmitContent ++ 
                                    lockFile.Options.Settings.OmitContent  })
            |> Map.ofSeq


        let usedPackages =
            let d = ref usedPackages

            for name,settings in usedPackages |> Seq.collect (fun u -> lookup.[NormalizedPackageName u.Key] |> Seq.map (fun x -> x,u.Value)) do
                if (!d).ContainsKey name |> not then
                  d := Map.add name settings !d

            !d

        let usedPackageSettings =
            usedPackages
            |> Seq.map (fun u -> NormalizedPackageName u.Key,u.Value)
            |> Map.ofSeq

        project.UpdateReferences(model, usedPackageSettings, options.Hard)

        removeCopiedFiles project

        let gitRemoteItems =
            referenceFile.RemoteFiles
            |> List.map (fun file ->
                             let link = if file.Link = "." then Path.GetFileName file.Name else Path.Combine(file.Link, Path.GetFileName file.Name)
                             let remoteFilePath = 
                                if verbose then
                                    tracefn "FileName: %s " file.Name 

                                match lockFile.SourceFiles |> List.tryFind (fun f -> Path.GetFileName(f.Name) = file.Name) with
                                | Some file -> file.FilePath root
                                | None -> failwithf "%s references file %s, but it was not found in the paket.lock file." referenceFile.FileName file.Name

                             let linked = defaultArg file.Settings.Link true

                             if linked then
                                 { BuildAction = project.DetermineBuildAction file.Name
                                   Include = createRelativePath project.FileName remoteFilePath
                                   Link = Some link }
                             else
                                 let toDir = Path.GetDirectoryName(project.FileName)
                                 let targetFile = FileInfo(Path.Combine(toDir,link))
                                 if targetFile.Directory.Exists |> not then
                                    targetFile.Directory.Create()

                                 File.Copy(remoteFilePath,targetFile.FullName)

                                 { BuildAction = project.DetermineBuildAction file.Name
                                   Include = createRelativePath project.FileName targetFile.FullName
                                   Link = None })

        let nuGetFileItems =
            copyContentFiles(project, findPackagesWithContent(root,usedPackages))
            |> List.map (fun file ->
                                { BuildAction = project.DetermineBuildAction file.Name
                                  Include = createRelativePath project.FileName file.FullName
                                  Link = None })

        project.UpdateFileItems(gitRemoteItems @ nuGetFileItems, options.Hard)

        project.Save()

    if options.Redirects || lockFile.Options.Redirects then
        applyBindingRedirects root extractedPackages

/// Installs all packages from the lock file.
let Install(sources, options : InstallerOptions, lockFile : LockFile) =
    let root = FileInfo(lockFile.FileName).Directory.FullName
    let projects = findAllReferencesFiles root |> returnOrFail
    InstallIntoProjects(sources, options, lockFile, projects)
