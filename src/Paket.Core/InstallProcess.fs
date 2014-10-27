﻿/// Contains methods for the install process.
module Paket.InstallProcess

open Paket
open Paket.Logging
open Paket.ModuleResolver
open Paket.PackageResolver
open System.IO
open System.Collections.Generic
open FSharp.Polyfill

let private findPackagesWithContent (usedPackages:Dictionary<_,_>) = 
    usedPackages
    |> Seq.map (fun kv -> DirectoryInfo(Path.Combine("packages", kv.Key)))
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
    |> List.filter (fun fi -> not <| fi.FullName.Contains("paket-files"))
    |> removeFilesAndTrimDirs

let CreateInstallModel(sources, force, package) = 
    async { 
        let! (package, files) = RestoreProcess.ExtractPackage(sources, force, package)
        let nuspec = FileInfo(sprintf "./packages/%s/%s.nuspec" package.Name package.Name)
        let nuspec = Nuspec.Load nuspec.FullName
        let files = files |> Seq.map (fun fi -> fi.FullName)
        return package, InstallModel.CreateFromLibs(package.Name, package.Version, files, nuspec)
    }

/// Restores the given packages from the lock file.
let createModel(sources,force, lockFile:LockFile) = 
    let sourceFileDownloads =
        lockFile.SourceFiles
        |> Seq.map (fun file -> GitHub.DownloadSourceFile(Path.GetDirectoryName lockFile.FileName, file))        
        |> Async.Parallel

    let packageDownloads = 
        lockFile.ResolvedPackages
        |> Seq.map (fun kv -> CreateInstallModel(sources,force,kv.Value))
        |> Async.Parallel

    let _,extractedPackages =
        Async.Parallel(sourceFileDownloads,packageDownloads)
        |> Async.RunSynchronously

    extractedPackages

/// Installs the given all packages from the lock file.
let Install(sources,force, hard, lockFile:LockFile, useTargets) = 
    let extractedPackages = createModel(sources,force, lockFile)

    let model =
        extractedPackages
        |> Array.map (fun (p,m) -> p.Name.ToLower(),m)
        |> Map.ofArray

    let applicableProjects =
        ProjectFile.FindAllProjects(".") 
        |> List.choose (fun p -> ProjectFile.FindReferencesFile (FileInfo(p.FileName))
                                 |> Option.map (fun r -> p, ReferencesFile.FromFile(r)))

    for project,referenceFile in applicableProjects do    
        verbosefn "Installing to %s" project.FileName

        let usedPackages = lockFile.GetPackageHull(referenceFile)

        project.UpdateReferences(model,usedPackages,hard, useTargets)
        
        removeCopiedFiles project

        let getGitHubFilePath name = 
            (lockFile.SourceFiles |> List.find (fun f -> Path.GetFileName(f.Name) = name)).FilePath

        let gitHubFileItems =
            referenceFile.GitHubFiles
            |> List.map (fun file -> 
                             { BuildAction = project.DetermineBuildAction file.Name 
                               Include = createRelativePath project.FileName (getGitHubFilePath file.Name)
                               Link = Some(if file.Link = "." then Path.GetFileName(file.Name)
                                           else Path.Combine(file.Link, Path.GetFileName(file.Name))) })
        
        let nuGetFileItems =
            if lockFile.Options.OmitContent then [] else
            let files = copyContentFiles(project, findPackagesWithContent usedPackages)
            files |> List.map (fun file -> 
                                    { BuildAction = project.DetermineBuildAction file.Name
                                      Include = createRelativePath project.FileName file.FullName
                                      Link = None })

        project.UpdateFileItems(gitHubFileItems @ nuGetFileItems, hard)

        project.Save()
