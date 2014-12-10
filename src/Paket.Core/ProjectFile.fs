﻿namespace Paket

open Paket.Domain
open Paket.Logging
open System
open System.IO
open System.Xml
open System.Collections.Generic
open Paket.Xml

type FileItem = 
    { BuildAction : string
      Include : string 
      Link : string option }

type ProjectReference = 
    { Path : string
      Name : string
      GUID : Guid
      Private : bool }

[<RequireQualifiedAccess>]
type ProjectOutputType =
| Exe 
| Library

/// Contains methods to read and manipulate project files.
type ProjectFile = 
    { FileName: string
      OriginalText : string
      Document : XmlDocument
      ProjectNode : XmlNode }

    member private this.FindNodes paketOnes name =
        [for node in this.Document |> getDescendants name do
            let isPaketNode = ref false
            for child in node.ChildNodes do
                if child.Name = "Paket" then isPaketNode := true

            if !isPaketNode = paketOnes then yield node]

    member this.Name = FileInfo(this.FileName).Name

    member this.GetCustomReferenceAndFrameworkNodes() = this.FindNodes false "Reference"

    member this.TargetFileName = 
        let fi = FileInfo(this.FileName)
        let name = fi.Name.Replace(fi.Extension,Constants.PackageTargetsFileName)
        Path.Combine(fi.Directory.FullName,name)

    /// Finds all project files
    static member FindAllProjects(folder) = 
        FindAllFiles(folder, "*.*proj")
        |> Array.filter (fun f -> f.Extension = ".csproj" || f.Extension = ".fsproj" || f.Extension = ".vbproj")
        |> Array.choose (fun fi -> ProjectFile.Load fi.FullName)

    static member FindReferencesFile (projectFile : FileInfo) =
        let specificReferencesFile = FileInfo(Path.Combine(projectFile.Directory.FullName, projectFile.Name + "." + Constants.ReferencesFile))
        if specificReferencesFile.Exists then Some specificReferencesFile.FullName
        else
            let rec findInDir (currentDir:DirectoryInfo) = 
                let generalReferencesFile = FileInfo(Path.Combine(currentDir.FullName, Constants.ReferencesFile))
                if generalReferencesFile.Exists then Some generalReferencesFile.FullName
                elif (FileInfo(Path.Combine(currentDir.FullName, Constants.DependenciesFileName))).Exists then None
                elif currentDir.Parent = null then None
                else findInDir currentDir.Parent 
                    
            findInDir projectFile.Directory

    member this.CreateNode(name) = 
        this.Document.CreateElement(name, Constants.ProjectDefaultNameSpace)

    member this.CreateNode(name, text) = 
        let node = this.CreateNode(name)
        node.InnerText <- text
        node

    member this.DeleteIfEmpty name =
        let nodesToDelete = List<_>()
        for node in this.Document |> getDescendants name do
            if node.ChildNodes.Count = 0 then
                nodesToDelete.Add node

        for node in nodesToDelete do
            node.ParentNode.RemoveChild(node) |> ignore

    member this.FindPaketNodes(name) = this.FindNodes true name

    member this.GetFrameworkAssemblies() = 
        [for node in this.Document |> getDescendants "Reference" do
            let hasHintPath = ref false
            for child in node.ChildNodes do
                if child.Name = "HintPath" then 
                    hasHintPath := true
            if not !hasHintPath then
                yield node.Attributes.["Include"].InnerText.Split(',').[0] ]

    member this.DeletePaketNodes(name) =    
        let nodesToDelete = this.FindPaketNodes(name) 
        if nodesToDelete |> Seq.isEmpty |> not then
            verbosefn "    - Deleting Paket %s nodes" name

        for node in nodesToDelete do
            node.ParentNode.RemoveChild(node) |> ignore

    member this.createFileItemNode fileItem =
        this.CreateNode(fileItem.BuildAction)
        |> addAttribute "Include" fileItem.Include
        |> addChild (this.CreateNode("Paket","True"))
        |> (fun n -> match fileItem.Link with
                     | Some link -> addChild (this.CreateNode("Link" ,link.Replace("\\","/"))) n
                     | _ -> n)

    member this.UpdateFileItems(fileItems : FileItem list, hard) = 
        this.DeletePaketNodes("Compile")
        this.DeletePaketNodes("Content")

        let firstItemGroup = this.ProjectNode |> getNodes "ItemGroup" |> Seq.firstOrDefault

        let newItemGroups = 
            match firstItemGroup with
            | None ->
                ["Content", this.CreateNode("ItemGroup")
                 "Compile", this.CreateNode("ItemGroup") ] 
            | Some node ->
                ["Content", node :?> XmlElement
                 "Compile", node :?> XmlElement ] 
            |> dict

        for fileItem in fileItems |> List.rev do
            let paketNode = this.createFileItemNode fileItem

            let fileItemsInSameDir =
                this.Document 
                |> getDescendants fileItem.BuildAction
                |> List.filter (fun node -> 
                    match node |> getAttribute "Include" with
                    | Some path when path.StartsWith(Path.GetDirectoryName(fileItem.Include)) ->
                        true
                    | _ -> false)

            if fileItemsInSameDir |> Seq.isEmpty 
            then 
                newItemGroups.[fileItem.BuildAction].PrependChild(paketNode) |> ignore
            else
                let existingNode = fileItemsInSameDir 
                                   |> Seq.tryFind (fun n -> n.Attributes.["Include"].Value = fileItem.Include)
                match existingNode with
                | Some existingNode ->
                    if hard 
                    then 
                        if not <| (existingNode.ChildNodes |> Seq.cast<XmlNode> |> Seq.exists (fun n -> n.Name = "Paket"))
                        then existingNode :?> XmlElement |> addChild (this.CreateNode("Paket","True")) |> ignore
                    else verbosefn "  - custom nodes for %s in %s ==> skipping" fileItem.Include this.FileName
                | None  ->
                    let firstNode = fileItemsInSameDir |> Seq.head
                    firstNode.ParentNode.InsertBefore(paketNode, firstNode) |> ignore
        
        this.DeleteIfEmpty("ItemGroup")

    member this.GetCustomModelNodes(model:InstallModel) =
        let libs = model.GetReferenceNames()
        
        this.GetCustomReferenceAndFrameworkNodes()
        |> List.filter (fun node -> Set.contains (node.Attributes.["Include"].InnerText.Split(',').[0]) libs)
    
    member this.DeleteCustomModelNodes(model:InstallModel) =
        let nodesToDelete = 
            this.GetCustomModelNodes(model)
            |> List.filter (fun node ->
                let isFrameworkNode = ref true
                for child in node.ChildNodes do
                    if child.Name = "HintPath" then isFrameworkNode := false

                not !isFrameworkNode)
        
        if nodesToDelete <> [] then
            let (PackageName name) = model.PackageName
            verbosefn "    - Deleting custom projects nodes for %s" name

        for node in nodesToDelete do            
            node.ParentNode.RemoveChild(node) |> ignore

    member this.DeletePaketImportNodes(fileName) =
        let nodesToDelete = 
            [for node in this.Document |> getDescendants "Import" do                
                let attr = node.Attributes.["Project"]
                if attr <> null && attr.InnerText = fileName then
                    yield node]

        for node in nodesToDelete do            
            node.ParentNode.RemoveChild(node) |> ignore


    member this.GenerateTargetImport(filename:string) =        
        let relativePath = createRelativePath this.FileName filename 

        let importNode = this.Document.CreateElement("Import", Constants.ProjectDefaultNameSpace)
        let condition = sprintf "Exists('%s')" relativePath
        importNode |> addAttribute "Project" relativePath |> ignore
        importNode |> addAttribute "Condition" condition |> ignore
        importNode

    member this.GenerateXml(model:InstallModel) =
        let references = 
            this.GetCustomReferenceAndFrameworkNodes()
            |> List.map (fun node -> node.Attributes.["Include"].InnerText.Split(',').[0])
            |> Set.ofList

        let model = model.FilterReferences(references)
        let createItemGroup references = 
            let itemGroup = this.CreateNode("ItemGroup")
                                
            for lib in references do
                match lib with
                | Reference.Library lib ->
                    let fi = new FileInfo(normalizePath lib)
                    
                    this.CreateNode("Reference")
                    |> addAttribute "Include" (fi.Name.Replace(fi.Extension,""))
                    |> addChild (this.CreateNode("HintPath", createRelativePath this.FileName fi.FullName))
                    |> addChild (this.CreateNode("Private","True"))
                    |> addChild (this.CreateNode("Paket","True"))
                    |> itemGroup.AppendChild
                    |> ignore
                | Reference.FrameworkAssemblyReference frameworkAssembly ->              
                    this.CreateNode("Reference")
                    |> addAttribute "Include" frameworkAssembly
                    |> addChild (this.CreateNode("Paket","True"))
                    |> itemGroup.AppendChild
                    |> ignore
            itemGroup

        let conditions =
            model.LibFolders
            |> List.map (fun lib -> PlatformMatching.getCondition lib.Targets,createItemGroup lib.Files.References)
            |> List.sortBy fst

        match conditions with
        |  ["$(TargetFrameworkIdentifier) == 'true'",itemGroup] -> itemGroup
        |  _ ->
            let chooseNode = this.CreateNode("Choose")

            conditions
            |> List.map (fun (condition,itemGroup) ->
                let whenNode = 
                    this.CreateNode("When")
                    |> addAttribute "Condition" condition                
               
                whenNode.AppendChild(itemGroup) |> ignore
                whenNode)
            |> List.iter(fun node -> chooseNode.AppendChild(node) |> ignore)

            chooseNode
        

    member this.GenerateReferences(rootPath:string,completeModel: Map<NormalizedPackageName,InstallModel>, usedPackages : Set<NormalizedPackageName>, hard) = 
        let generateTargetsFiles = true // TODO: Make parameter

        this.DeletePaketNodes("Reference")
        this.DeletePaketImportNodes(createRelativePath this.FileName this.TargetFileName)
        
        ["ItemGroup";"When";"Otherwise";"Choose";"When";"Choose"]
        |> List.iter this.DeleteIfEmpty

        let targetsDocument = XmlDocument()
        let project = targetsDocument.CreateElement("Project", Constants.ProjectDefaultNameSpace)
        
        let referenced = ref false
            
        completeModel
        |> Seq.filter (fun kv -> usedPackages.Contains kv.Key)
        |> Seq.map (fun kv -> 
            if hard then
                this.DeleteCustomModelNodes(kv.Value)

            kv.Value.PackageName,this.GenerateXml kv.Value)
        |> Seq.filter (fun (_,node) -> node.ChildNodes.Count > 0)
        |> Seq.iter (fun (packageName,node) ->
            if generateTargetsFiles then
                if not !referenced then
                    let targetsNode = this.GenerateTargetImport this.TargetFileName
                    this.ProjectNode.AppendChild targetsNode |> ignore
                    referenced := true

                let tempNode = targetsDocument.ImportNode(node, true)
                project.AppendChild tempNode |> ignore
                targetsDocument.AppendChild project |> ignore
            else
                this.ProjectNode.AppendChild node |> ignore)
        targetsDocument
    
    member this.UpdateReferences(rootPath:string,completeModel: Map<NormalizedPackageName,InstallModel>, usedPackages : Set<NormalizedPackageName>, hard) =
        let targetsDocument = this.GenerateReferences(rootPath,completeModel, usedPackages, hard)
        let fi = FileInfo(this.TargetFileName)
        let originalText = 
            if fi.Exists then
                try 
                    let originalDoc = new XmlDocument()
                    originalDoc.Load fi.FullName
                    Utils.normalizeXml originalDoc
                with
                | _ -> ""
            else
                ""

        let newText = Utils.normalizeXml targetsDocument
            
        if newText <> originalText then
            targetsDocument.Save(this.TargetFileName)
            this.Save() // Save in order to make Visual Studio reload the project
                
    member this.Save() = this.Document.Save(this.FileName)

    member this.SaveIfChanged() =
        let currentText = Utils.normalizeXml this.Document
        if currentText <> this.OriginalText then 
            verbosefn "Project %s changed" this.FileName
            this.Save() 

    member this.GetPaketFileItems() =
        this.FindPaketNodes("Content")
        |> List.append <| this.FindPaketNodes("Compile")
        |> List.map (fun n -> FileInfo(Path.Combine(Path.GetDirectoryName(this.FileName), n.Attributes.["Include"].Value)))

    member this.GetInterProjectDependencies() =  
        let forceGetInnerText node name =
            match node |> getNode name with 
            | Some n -> n.InnerText
            | None -> failwithf "unable to parse %s" node.Name

        [for n in this.Document |> getDescendants "ProjectReference" -> 
            { Path = n.Attributes.["Include"].Value
              Name = forceGetInnerText n "Name"
              GUID =  forceGetInnerText n "Project" |> Guid.Parse
              Private =  forceGetInnerText n "Private" |> bool.Parse }]

    member this.ReplaceNuGetPackagesFile() =
        let noneNodes = this.Document |> getDescendants "None"
        match noneNodes |> List.tryFind (fun n -> n |> getAttribute "Include" = Some "packages.config") with
        | None -> ()
        | Some nugetNode ->
            match noneNodes |> List.filter (fun n -> n |> getAttribute "Include" = Some Constants.ReferencesFile) with 
            | [_] -> nugetNode.ParentNode.RemoveChild(nugetNode) |> ignore
            | [] -> nugetNode.Attributes.["Include"].Value <- Constants.ReferencesFile
            | _::_ -> failwithf "multiple %s nodes in project file %s" Constants.ReferencesFile this.FileName

    member this.RemoveNuGetTargetsEntries() =
        let toDelete = 
            [ this.Document |> getDescendants "RestorePackages" |> Seq.firstOrDefault
              this.Document 
              |> getDescendants "Import" 
              |> List.tryFind (fun n -> n |> getAttribute "Project" = Some "$(SolutionDir)\\.nuget\\nuget.targets")
              this.Document
              |> getDescendants "Target"
              |> List.tryFind (fun n -> n |> getAttribute "Name" = Some "EnsureNuGetPackageBuildImports") ]
            |> List.choose id
        
        toDelete
        |> List.iter 
            (fun node -> 
                let parent = node.ParentNode
                node.ParentNode.RemoveChild node |> ignore
                if not parent.HasChildNodes then 
                    parent.ParentNode.RemoveChild parent |> ignore)

    member this.OutputType =
        seq {for outputType in this.Document |> getDescendants "OutputType" ->
                match outputType.InnerText with
                | "Exe" -> ProjectOutputType.Exe
                | _     -> ProjectOutputType.Library }
        |> Seq.head

    member this.GetTargetFramework() =
        seq {for outputType in this.Document |> getDescendants "TargetFrameworkVersion" ->
                outputType.InnerText  }
        |> Seq.map (fun s -> // TODO make this a separate function
                        s.Replace("v","net")
                        |> FrameworkIdentifier.Extract)                        
        |> Seq.map (fun o -> o.Value)
        |> Seq.head
    
    member this.AddImportForPaketTargets(relativeTargetsPath) =
        match this.Document 
              |> getDescendants "Import" 
              |> List.tryFind (fun n -> n |> getAttribute "Project" = Some relativeTargetsPath) with
        | Some _ -> ()
        | None -> 
            let node = this.CreateNode("Import") |> addAttribute "Project" relativeTargetsPath
            this.ProjectNode.AppendChild(node) |> ignore

    member this.DetermineBuildAction fileName =
        if Path.GetExtension(this.FileName) = Path.GetExtension(fileName) + "proj" 
        then "Compile"
        else "Content"

    static member Load(fileName:string) =
        try
            let fi = FileInfo(fileName)
            let doc = new XmlDocument()
            doc.Load fi.FullName

            let manager = new XmlNamespaceManager(doc.NameTable)
            manager.AddNamespace("ns", Constants.ProjectDefaultNameSpace)
            let projectNode = 
                match doc |> getNode "Project" with
                | Some node -> node
                | _ -> failwith "unable to find Project node in file %s" fileName
            Some { FileName = fi.FullName; Document = doc; ProjectNode = projectNode; OriginalText = Utils.normalizeXml doc }
        with
        | exn -> 
            traceWarnfn "Unable to parse %s:%s      %s" fileName Environment.NewLine exn.Message
            None