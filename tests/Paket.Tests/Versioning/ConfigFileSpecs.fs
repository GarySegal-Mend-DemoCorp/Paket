﻿[<NUnit.Framework.TestFixture(Category=Category.Versioning)>]
module Paket.ConfigFileSpecs

open Paket
open Paket.ConfigFile
open NUnit.Framework
open System.Xml
open FsUnit

#nowarn "25"

let sampleDoc() =
    let doc = XmlDocument()
    doc.LoadXml """<?xml version="1.0" encoding="utf-8"?>
<credentials>
</credentials>
""" 
    doc

[<Test>]
let ``get username and password from node``() = 
    let doc = sampleDoc()
    let node = doc.CreateElement("credential")
    node.SetAttribute("username", "demo-user")
    let salt, password = Encrypt "demopassword"
    node.SetAttribute("password", password)
    node.SetAttribute("salt", salt)
    // Act
    let (Credentials(username, password)) = getAuthFromNode node

    // Assert
    username |> shouldEqual  "demo-user"
    password |> shouldEqual  "demopassword"

    
[<Test>]
let ``get source nodes``() = 
    let doc = sampleDoc()
    let node = doc.CreateElement("credential")
    node.SetAttribute("source", "wrongnode")
    doc.DocumentElement.AppendChild(node) |> ignore
    let node = doc.CreateElement("credential")
    node.SetAttribute("source", "goodnode")
    doc.DocumentElement.AppendChild(node) |> ignore
    // Act
    let nodes = getSourceNodes doc "goodnode" "credential"

    // Assert
    nodes.Length |> shouldEqual 1
    nodes.Head.Attributes.["source"].Value |> shouldEqual  "goodnode"

   
[<Test>]
let ``get token from node``() = 
   let doc = sampleDoc()
   let node = doc.CreateElement "token"
   node.SetAttribute("value", "demotoken")
   let (Token token) = getAuthFromNode node

   token |> shouldEqual "demotoken"