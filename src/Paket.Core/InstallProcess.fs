﻿/// Contains methods for the install process.
module Paket.InstallProcess

open Paket
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

let private findPackagesWithContent (root,usedPackages:HashSet<_>) = 
    usedPackages
    |> Seq.map (fun (PackageName x) -> DirectoryInfo(Path.Combine(root, "packages", x)))
    |> Seq.choose (fun packageDir -> packageDir.GetDirectories("Content") |> Array.tryFind (fun _ -> true))
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

/// Downloads and extracts a package.
let ExtractPackage(root, sources, force, package : ResolvedPackage) = 
    async { 
        let (PackageName name) = package.Name
        let v = package.Version
        match package.Source with
        | PackageSources.Nuget source -> 
            let auth = 
                sources |> List.tryPick (fun s -> 
                               match s with
                               | PackageSources.Nuget s -> s.Authentication |> Option.map PackageSources.toBasicAuth
                               | _ -> None)
            try 
                let! folder = NuGetV2.DownloadPackage(root, auth, source.Url, name, v, force)
                return package, NuGetV2.GetLibFiles folder
            with _ when force = false -> 
                tracefn "Something went wrong with the download of %s %A - automatic retry with --force." name v
                let! folder = NuGetV2.DownloadPackage(root, auth, source.Url, name, v, true)
                return package, NuGetV2.GetLibFiles folder
        | PackageSources.LocalNuget path -> 
            let packageFile = Path.Combine(root, path, sprintf "%s.%A.nupkg" name v)
            let! folder = NuGetV2.CopyFromCache(root, packageFile, name, v, force)
            return package, NuGetV2.GetLibFiles folder
    }

let CreateInstallModel(root, sources, force, package) = 
    async { 
        let! (package, files) = ExtractPackage(root, sources, force, package)
        let (PackageName name) = package.Name
        let nuspec = FileInfo(sprintf "%s/packages/%s/%s.nuspec" root name name)
        let nuspec = Nuspec.Load nuspec.FullName
        let files = files |> Seq.map (fun fi -> fi.FullName)
        return package, InstallModel.CreateFromLibs(package.Name, package.Version, package.FrameworkRestriction, files, nuspec)
    }

/// Restores the given packages from the lock file.
let createModel(root, sources,force, lockFile:LockFile) = 
    let sourceFileDownloads = RemoteDownload.DownloadSourceFiles(root, lockFile.SourceFiles)
        
    let packageDownloads = 
        lockFile.ResolvedPackages
        |> Seq.map (fun kv -> CreateInstallModel(root,sources,force,kv.Value))
        |> Async.Parallel

    let _,extractedPackages =
        Async.Parallel(sourceFileDownloads,packageDownloads)
        |> Async.RunSynchronously

    extractedPackages

/// Picks the highest version of a library
let private pickHighestLibraryVersion libraries = 
    libraries
    |> Seq.sortBy (fun p -> FileVersionInfo.GetVersionInfo(p).FileVersion)
    |> Seq.last

/// Applies binding redirects for all strong-named references to all app. and web. config files.
let private applyBindingRedirects root extractedPackages =
    extractedPackages
    |> Seq.map(fun (package, model:InstallModel) -> model.GetReferences.Force())
    |> Set.unionMany
    |> Seq.choose(fun ref -> 
            match ref with
            | Reference.Library path -> Some path
            | _-> None)
    |> Seq.groupBy (fun p -> FileInfo(p).Name)
    |> Seq.map(fun (_,libraries) ->  pickHighestLibraryVersion libraries)
    |> Seq.choose(fun assemblyFileName ->
        try
            let assembly = Assembly.ReflectionOnlyLoadFrom assemblyFileName
            assembly
            |> BindingRedirects.getPublicKeyToken
            |> Option.map(fun token -> assembly, token)
        with exn -> None)
    |> Seq.map(fun (assembly, token) ->
        {   BindingRedirect.AssemblyName = assembly.GetName().Name
            Version = assembly.GetName().Version.ToString()
            PublicKeyToken = token
            Culture = None })
    |> applyBindingRedirectsToFolder root

/// Installs the given all packages from the lock file.
let Install(sources,force, hard, withBindingRedirects, lockFile:LockFile) = 
    let root = FileInfo(lockFile.FileName).Directory.FullName 
    let extractedPackages = createModel(root,sources,force, lockFile)

    let model =
        extractedPackages
        |> Array.map (fun (p,m) -> NormalizedPackageName p.Name,m)
        |> Map.ofArray

    let applicableProjects =
        root
        |> ProjectFile.FindAllProjects
        |> Array.choose (fun p -> ProjectFile.FindReferencesFile(FileInfo(p.FileName))
                                  |> Option.map (fun r -> p, ReferencesFile.FromFile(r)))

    for project, referenceFile in applicableProjects do    
        verbosefn "Installing to %s" project.FileName

        let usedPackages = lockFile.GetPackageHull(referenceFile)

        let usedPackageNames =
            usedPackages
            |> Seq.map (fun x -> NormalizedPackageName x)
            |> Set.ofSeq

        project.UpdateReferences(lockFile.Directory,model,usedPackageNames,hard)
        
        removeCopiedFiles project

        let getSingleRemoteFilePath name = 
            tracefn "Filename %s " name
            lockFile.SourceFiles |> List.iter (fun i -> tracefn " %s %s " i.Name  i.FilePath)
            let sourceFile = lockFile.SourceFiles |> List.tryFind (fun f -> Path.GetFileName(f.Name) = name)
            match sourceFile with
            | Some file -> file.FilePath
            | None -> failwithf "%s references file %s, but it was not found in the paket.lock file." referenceFile.FileName name

        let gitRemoteItems =
            referenceFile.RemoteFiles
            |> List.map (fun file -> 
                             { BuildAction = project.DetermineBuildAction file.Name 
                               Include = createRelativePath project.FileName (getSingleRemoteFilePath file.Name)
                               Link = Some(if file.Link = "." then Path.GetFileName(file.Name)
                                           else Path.Combine(file.Link, Path.GetFileName(file.Name))) })
        
        let nuGetFileItems =
            if lockFile.Options.OmitContent then [] else
            copyContentFiles(project, findPackagesWithContent(root,usedPackages))
            |> List.map (fun file -> 
                                { BuildAction = project.DetermineBuildAction file.Name
                                  Include = createRelativePath project.FileName file.FullName
                                  Link = None })

        project.UpdateFileItems(gitRemoteItems @ nuGetFileItems, hard)

        project.SaveIfChanged()

    if withBindingRedirects || lockFile.Options.Redirects then
        applyBindingRedirects root extractedPackages
