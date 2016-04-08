﻿module Paket.DependencyChangeDetection

open Paket.Domain
open Paket.Requirements
open Paket.PackageResolver

let findNuGetChangesInDependenciesFile(dependenciesFile:DependenciesFile,lockFile:LockFile) =
    let allTransitives groupName = lockFile.GetTransitiveDependencies groupName
    let hasChanged groupName transitives (newRequirement:PackageRequirement) (originalPackage:ResolvedPackage) =
        let settingsChanged() =
            if newRequirement.Settings <> originalPackage.Settings then
                if newRequirement.Settings.FrameworkRestrictions <> originalPackage.Settings.FrameworkRestrictions then
                    transitives |> Seq.contains originalPackage.Name |> not
                else true
            else false

        newRequirement.VersionRequirement.IsInRange originalPackage.Version |> not || settingsChanged()

    let added groupName transitives =
        match dependenciesFile.Groups |> Map.tryFind groupName with
        | None -> Set.empty
        | Some group ->
            let lockFileGroup = lockFile.Groups |> Map.tryFind groupName 
            group.Packages
            |> Seq.map (fun d -> d.Name,d)
            |> Seq.filter (fun (name,pr) ->
                match lockFileGroup with
                | None -> true
                | Some group ->
                    match group.Resolution.TryFind name with
                    | Some p -> hasChanged groupName transitives pr p
                    | _ -> true)
            |> Seq.map (fun (p,_) -> groupName,p)
            |> Set.ofSeq
    
    let modified groupName transitives = 
        let directMap =
            match dependenciesFile.Groups |> Map.tryFind groupName with
            | None -> Map.empty
            | Some group ->
                group.Packages
                |> Seq.map (fun d -> d.Name,d)
                |> Map.ofSeq

        [for t in lockFile.GetTopLevelDependencies(groupName) do
            let name = t.Key
            match directMap.TryFind name with
            | Some pr -> if hasChanged groupName transitives pr t.Value then yield groupName, name // Modified
            | _ -> yield groupName, name // Removed
        ]
        |> List.map lockFile.GetAllNormalizedDependenciesOf
        |> Seq.concat
        |> Set.ofSeq

    let groupNames =
        dependenciesFile.Groups
        |> Seq.map (fun kv -> kv.Key)
        |> Seq.append (lockFile.Groups |> Seq.map (fun kv -> kv.Key))

    groupNames
    |> Seq.map (fun groupName -> 
            let transitives = allTransitives groupName
            let added = added groupName transitives
            let modified = modified groupName transitives
            Set.union added modified)
    |> Seq.concat
    |> Set.ofSeq

[<CustomEquality;CustomComparison>]
type RemoteFileChange =
    { Owner : string
      Project : string
      Name : string
      Origin : ModuleResolver.Origin
      Commit : string option
      AuthKey : string option }

    override this.Equals(that) = 
        match that with
        | :? RemoteFileChange as that -> 
            this.FieldsWithoutCommit = that.FieldsWithoutCommit &&
             ((this.Commit = that.Commit) || this.Commit = None || that.Commit = None)
        | _ -> false

    override this.ToString() = sprintf "%O/%s/%s" this.Origin this.Project this.Name

    member private this.FieldsWithoutCommit = this.Owner,this.Name,this.AuthKey,this.Project,this.Origin
    member private this.FieldsWithCommit = this.FieldsWithoutCommit,this.Commit
    override this.GetHashCode() = hash this.FieldsWithCommit

    static member Compare(x:RemoteFileChange,y:RemoteFileChange) =
        if x = y then 0 else
        compare x.FieldsWithCommit y.FieldsWithCommit

    interface System.IComparable with
       member this.CompareTo that = 
          match that with 
          | :? RemoteFileChange as that -> RemoteFileChange.Compare(this,that)
          | _ -> invalidArg "that" "cannot compare value of different types"

    static member CreateUnresolvedVersion (unresolved:ModuleResolver.UnresolvedSource) : RemoteFileChange =
        { Owner = unresolved.Owner
          Project = unresolved.Project
          Name = unresolved.Name
          Origin = unresolved.Origin
          Commit = 
            match unresolved.Version with
            | ModuleResolver.VersionRestriction.NoVersionRestriction -> None
            | ModuleResolver.VersionRestriction.Concrete x -> Some x
            | ModuleResolver.VersionRestriction.VersionRequirement vr -> Some(vr.ToString())

          AuthKey = unresolved.AuthKey }

    static member CreateResolvedVersion (resolved:ModuleResolver.ResolvedSourceFile) : RemoteFileChange =
        { Owner = resolved.Owner
          Project = resolved.Project
          Name = resolved.Name
          Origin = resolved.Origin
          Commit = Some resolved.Commit
          AuthKey = resolved.AuthKey }


let findRemoteFileChangesInDependenciesFile(dependenciesFile:DependenciesFile,lockFile:LockFile) =
    let groupNames =
        dependenciesFile.Groups
        |> Seq.map (fun kv -> kv.Key)
        |> Seq.append (lockFile.Groups |> Seq.map (fun kv -> kv.Key))

    groupNames
    |> Seq.map (fun groupName ->
            match dependenciesFile.Groups |> Map.tryFind groupName with
            | Some dependenciesFileGroup ->
                match lockFile.Groups |> Map.tryFind groupName with
                | Some lockFilegroup ->
                    let lockFileRemoteFiles =
                        lockFilegroup.RemoteFiles
                        |> List.map RemoteFileChange.CreateResolvedVersion
                        |> Set.ofList

                    let dependenciesFileRemoteFiles =
                        dependenciesFileGroup.RemoteFiles
                        |> List.map RemoteFileChange.CreateUnresolvedVersion
                        |> Set.ofList

                    let u =
                        dependenciesFileRemoteFiles
                        |> Set.union lockFileRemoteFiles
                    let i =
                        dependenciesFileRemoteFiles
                        |> Set.intersect lockFileRemoteFiles

                    Set.difference u i
                | None -> 
                    // all added
                    dependenciesFileGroup.RemoteFiles 
                    |> List.map RemoteFileChange.CreateUnresolvedVersion 
                    |> Set.ofList 
            | None -> 
                // all removed
                lockFile.GetGroup(groupName).RemoteFiles
                |> List.map RemoteFileChange.CreateResolvedVersion
                |> Set.ofList
            |> Set.map (fun x -> groupName,x))
    |> Seq.concat
    |> Set.ofSeq

let GetPreferredNuGetVersions (dependenciesFile:DependenciesFile,lockFile:LockFile) =
    lockFile.GetGroupedResolution()
    |> Seq.map (fun kv ->
        let lockFileSource = kv.Value.Source
        match dependenciesFile.Groups |> Map.tryFind (fst kv.Key) with
        | None -> kv.Key, (kv.Value.Version, lockFileSource)
        | Some group -> 
            match group.Sources |> List.tryFind (fun s -> s.Url = lockFileSource.Url) with
            | Some s -> kv.Key, (kv.Value.Version, s)
            | None -> kv.Key, (kv.Value.Version, kv.Value.Source))
    |> Map.ofSeq
