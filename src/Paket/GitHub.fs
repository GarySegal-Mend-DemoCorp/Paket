﻿module Paket.GitHub

open System.Net
open System
open Paket

/// Gets a single file from github.
let downloadFile remoteFile = 
    let url = sprintf "https://github.com/%s/%s/raw/%s/%s" remoteFile.Owner remoteFile.Project remoteFile.CommitWithDefault remoteFile.Path
    use wc = new WebClient()
    Uri url |> wc.AsyncDownloadString

