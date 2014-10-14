﻿namespace Paket

open Paket.Logging
open Paket.PackageResolver
open System
open System.IO
open System.Xml
open System.Collections.Generic
open Paket.Xml

type FileItem = 
    { BuildAction : string
      Include : string 
      Link : string option }

/// Contains methods to read and manipulate project files.
type ProjectFile = 
    { FileName: string
      OriginalText : string
      Document : XmlDocument
      ProjectNode : XmlNode
      Namespaces : XmlNamespaceManager }

    /// Finds all project files
    static member FindAllProjects(folder) = 
        ["*.csproj";"*.fsproj";"*.vbproj"]
        |> List.map (fun projectType -> FindAllFiles(folder, projectType) |> Seq.toList)
        |> List.concat
        |> List.choose (fun fi -> ProjectFile.Load fi.FullName)

    static member FindReferencesFile (projectFile : FileInfo) =
        let specificReferencesFile = FileInfo(Path.Combine(projectFile.Directory.FullName, projectFile.Name + "." + Constants.ReferencesFile))
        if specificReferencesFile.Exists then Some specificReferencesFile.FullName
        else 
            let generalReferencesFile = FileInfo(Path.Combine(projectFile.Directory.FullName, Constants.ReferencesFile))
            if generalReferencesFile.Exists then Some generalReferencesFile.FullName
            else None

    member this.DeleteIfEmpty xPath =
        let nodesToDelete = List<_>()
        for node in this.Document.SelectNodes(xPath, this.Namespaces) do
            if node.ChildNodes.Count = 0 then
                nodesToDelete.Add node

        for node in nodesToDelete do
            node.ParentNode.RemoveChild(node) |> ignore

    member this.FindPaketNodes(name) = 
        [
            for node in this.Document.SelectNodes(sprintf "//ns:%s" name, this.Namespaces) do
                let isPaketNode = ref false
                for child in node.ChildNodes do
                        if child.Name = "Paket" then isPaketNode := true
            
                if !isPaketNode then yield node
        ]

    member this.DeletePaketNodes(name) =    
        let nodesToDelete = this.FindPaketNodes(name) 
        if nodesToDelete |> Seq.isEmpty |> not then
            verbosefn "    - Deleting Paket %s nodes" name

        for node in nodesToDelete do
            node.ParentNode.RemoveChild(node) |> ignore

    member this.CreateNode(name) = this.Document.CreateElement(name, Constants.ProjectDefaultNameSpace)

    member this.CreateNode(name,text) = 
        let node = this.CreateNode(name)
        node.InnerText <- text
        node

    member this.DeleteEmptyReferences() = 
        this.DeleteIfEmpty("//ns:Project/ns:Choose/ns:When/ns:ItemGroup")
        this.DeleteIfEmpty("//ns:Project/ns:Choose/ns:When")
        this.DeleteIfEmpty("//ns:Project/ns:Choose")
        this.DeleteIfEmpty("//ns:ItemGroup")

    member this.createFileItemNode fileItem =
        this.CreateNode(fileItem.BuildAction)
        |> addAttribute "Include" fileItem.Include
        |> addChild (this.CreateNode("Paket","True"))
        |> (fun n -> match fileItem.Link with
                     | Some link -> addChild (this.CreateNode("Link",link.Replace("\\","/"))) n
                     | _ -> n)

    member this.UpdateFileItems(fileItems : list<FileItem>, hard) = 
        this.DeletePaketNodes("Compile")
        this.DeletePaketNodes("Content")

        let newItemGroups = ["Content", this.CreateNode("ItemGroup")
                             "Compile", this.CreateNode("ItemGroup") ] |> dict

        for fileItem in fileItems do
            let paketNode = this.createFileItemNode fileItem
            let xpath = sprintf "//ns:%s[starts-with(@Include, '%s')]" 
                                fileItem.BuildAction 
                                (Path.GetDirectoryName(fileItem.Include))
            let fileItemsInSameDir = this.Document.SelectNodes(xpath, this.Namespaces) |> Seq.cast<XmlNode>
            if fileItemsInSameDir |> Seq.isEmpty 
            then 
                newItemGroups.[fileItem.BuildAction].AppendChild(paketNode) |> ignore
            else
                let existingNode = fileItemsInSameDir 
                                   |> Seq.tryFind (fun n -> n.Attributes.["Include"].Value = fileItem.Include)
                match existingNode with
                | Some existingNode ->
                    if hard 
                    then 
                        if not <| (existingNode.ChildNodes |> Seq.cast<XmlNode> |> Seq.exists (fun n -> n.Name = "Paket"))
                        then existingNode :?> XmlElement |> addChild (this.CreateNode("Paket", "True")) |> ignore
                    else verbosefn "  - custom nodes for %s in %s ==> skipping" fileItem.Include this.FileName
                | None  ->
                    let firstNode = fileItemsInSameDir |> Seq.head
                    firstNode.ParentNode.InsertBefore(paketNode, firstNode) |> ignore
        
        let firstItemGroup = this.Document.SelectNodes("//ns:ItemGroup", this.Namespaces) |> Seq.cast<XmlNode> |> Seq.firstOrDefault
        for newItemGroup in newItemGroups.Values do
            if newItemGroup.HasChildNodes then 
                match firstItemGroup with
                | Some firstItemGroup -> firstItemGroup.ParentNode.InsertBefore(newItemGroup, firstItemGroup) |> ignore
                | None -> this.ProjectNode.AppendChild(newItemGroup) |> ignore

        this.DeleteIfEmpty("//ns:ItemGroup")

    member this.HasCustomNodes(model:InstallModel) =
        let libs = model.GetLibraryNames.Force()
        let hasCustom = ref false
        for node in this.Document.SelectNodes("//ns:Reference", this.Namespaces) do
            if Set.contains (node.Attributes.["Include"].InnerText.Split(',').[0]) libs then
                let isPaket = ref false
                for child in node.ChildNodes do
                    if child.Name = "Paket" then 
                        isPaket := true
                if not !isPaket then
                    hasCustom := true
            
        !hasCustom

    member this.DeleteCustomNodes(model:InstallModel) =
        let nodesToDelete = List<_>()
        
        let libs = model.GetLibraryNames.Force()
        for node in this.Document.SelectNodes("//ns:Reference", this.Namespaces) do
            if Set.contains (node.Attributes.["Include"].InnerText.Split(',').[0]) libs then          
                nodesToDelete.Add node

        if nodesToDelete |> Seq.isEmpty |> not then
            verbosefn "    - Deleting custom projects nodes for %s" model.PackageName

        for node in nodesToDelete do            
            node.ParentNode.RemoveChild(node) |> ignore

    member this.GenerateTargetImport(filename:string) =
        let fileFromSln = normalizePath "$(SolutionDir)/" + filename.Substring(filename.LastIndexOf("packages"))
        let importNode = this.Document.CreateElement("Import", Constants.ProjectDefaultNameSpace)
        let condition = sprintf "Exists('%s')" fileFromSln
        importNode |> addAttribute "Project" fileFromSln |> ignore
        importNode |> addAttribute "Condition" condition |> ignore
        importNode

    static member GenerateTarget(model:InstallModel) =
        let doc = XmlDocument()
        let project = doc.CreateElement("Project", Constants.ProjectDefaultNameSpace)
        let chooseNode = doc.CreateElement("Choose", Constants.ProjectDefaultNameSpace)
        project.AppendChild(chooseNode) |> ignore
        doc.AppendChild(project) |> ignore
        model.Frameworks 
        |> Seq.iter (fun kv -> 
            let whenNode = 
                createNode(doc,"When")
                |> addAttribute "Condition" (kv.Key.GetCondition())

            let itemGroup = createNode(doc,"ItemGroup")
                                
            for lib in kv.Value.References do
                let reference = 
                    match lib with
                    | Reference.Library lib ->
                        let fi = new FileInfo(normalizePath lib)
                        let libFromSln = normalizePath "$(SolutionDir)/" + fi.FullName.Substring(fi.FullName.LastIndexOf("packages"))
                    
                        createNode(doc,"Reference")
                        |> addAttribute "Include" (fi.Name.Replace(fi.Extension,""))
                        |> addChild (createNodeWithText(doc,"HintPath", libFromSln))
                        |> addChild (createNodeWithText(doc,"Private","True"))
                        |> addChild (createNodeWithText(doc,"Paket","True"))
                    | Reference.FrameworkAssemblyReference frameworkAssembly ->                    
                        createNode(doc,"Reference")
                        |> addAttribute "Include" frameworkAssembly
                        |> addChild (createNodeWithText(doc,"Paket","True"))

                itemGroup.AppendChild(reference) |> ignore

            whenNode.AppendChild(itemGroup) |> ignore
            chooseNode.AppendChild(whenNode) |> ignore)

        doc


    member this.UpdateReferences(completeModel: Map<string,InstallModel>, usedPackages : Dictionary<string,bool>, hard) = 
        this.DeletePaketNodes("Reference")  
        for kv in usedPackages do
            let packageName = kv.Key
            let installModel =   completeModel.[packageName.ToLower()]

            if hard then
                this.DeleteCustomNodes(installModel)

            if this.HasCustomNodes(installModel) then verbosefn "  - custom nodes for %s ==> skipping" packageName else
            let targetDoc = ProjectFile.GenerateTarget(installModel)
            let paketTarget = FileInfo(sprintf "./packages/%s/Paket.targets" packageName)
            targetDoc.Save(paketTarget.FullName)
            let targetImport = this.GenerateTargetImport(paketTarget.FullName)
            this.ProjectNode.AppendChild(targetImport) |> ignore

        this.DeleteEmptyReferences()

    member this.Save() =
        if Utils.normalizeXml this.Document <> this.OriginalText then 
            verbosefn "Project %s changed" this.FileName
            this.Document.Save(this.FileName)

    member this.GetPaketFileItems() =
        this.FindPaketNodes("Content")
        |> List.append <| this.FindPaketNodes("Compile")
        |> List.map (fun n ->  FileInfo(Path.Combine(Path.GetDirectoryName(this.FileName), n.Attributes.["Include"].Value)))

    member this.ReplaceNugetPackagesFile() =
        let nugetNode = this.Document.SelectSingleNode("//ns:*[@Include='packages.config']", this.Namespaces)
        if nugetNode = null then () else
        match [for node in this.Document.SelectNodes("//ns:*[@Include='" + Constants.ReferencesFile + "']", this.Namespaces) -> node] with 
        | [_] -> nugetNode.ParentNode.RemoveChild(nugetNode) |> ignore
        | [] -> nugetNode.Attributes.["Include"].Value <- Constants.ReferencesFile
        | _::_ -> failwithf "multiple %s nodes in project file %s" Constants.ReferencesFile this.FileName

    member this.RemoveNugetTargetsEntries() =
        let toDelete = 
            [ this.Document.SelectNodes("//ns:RestorePackages", this.Namespaces)
              this.Document.SelectNodes("//ns:Import[@Project='$(SolutionDir)\\.nuget\\nuget.targets']", this.Namespaces) 
              this.Document.SelectNodes("//ns:Target[@Name='EnsureNuGetPackageBuildImports']", this.Namespaces)]
            |> List.map (Seq.cast<XmlNode> >> Seq.firstOrDefault)
        toDelete
        |> List.iter 
            (Option.iter 
                (fun node -> 
                     let parent = node.ParentNode
                     node.ParentNode.RemoveChild(node) |> ignore
                     if not parent.HasChildNodes then parent.ParentNode.RemoveChild(parent) |> ignore))
    
    member this.AddImportForPaketTargets(relativeTargetsPath) =
        match this.Document.SelectNodes(sprintf "//ns:Import[@Project='%s']" relativeTargetsPath, this.Namespaces)
                            |> Seq.cast |> Seq.firstOrDefault with
        | Some _ -> ()
        | None -> 
            let node = this.CreateNode("Import") |> addAttribute "Project" relativeTargetsPath
            this.Document.SelectSingleNode("//ns:Project", this.Namespaces).AppendChild(node) |> ignore

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
            let projectNode = doc.SelectNodes("//ns:Project", manager).[0]
            Some { FileName = fi.FullName; Document = doc; ProjectNode = projectNode; Namespaces = manager; OriginalText = Utils.normalizeXml doc }
        with
        | exn -> 
            traceWarnfn "Unable to parse %s:%s      %s" fileName Environment.NewLine exn.Message
            None