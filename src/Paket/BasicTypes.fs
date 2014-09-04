﻿namespace Paket

type Bound = 
    | Open
    | Closed

/// Represents version information.
type VersionRange = 
    | Latest
    | Specific of SemVerInfo
    | Minimum of SemVerInfo
    | GreaterThan of SemVerInfo
    | Maximum of SemVerInfo
    | LessThan of SemVerInfo
    | Range of fromB : Bound * from : SemVerInfo * _to : SemVerInfo * _toB : Bound
    /// Checks wether the given version is in the version range
    member this.IsInRange(version:string) =
        this.IsInRange(SemVer.parse version)

    /// Checks wether the given version is in the version range
    member this.IsInRange(version:SemVerInfo) =
        match this with
        | Latest -> true
        | Minimum v -> v <= version
        | Specific v -> v = version
        | Range(_, min, max, _) -> version >= min && version < max

    static member AtLeast version = Minimum(SemVer.parse version)

    static member Exactly version = Specific(SemVer.parse version)

    static member Between(version1,version2) = Range(Closed, SemVer.parse version1, SemVer.parse version2, Open)


/// Represents a resolver strategy.
type ResolverStrategy =
| Max
| Min

/// Different sources that packages can come from
type PackageSource =
| Nuget

/// Represents a package.
type Package = 
    { Name : string
      DirectDependencies : string list
      VersionRange : VersionRange
      ResolverStrategy : ResolverStrategy
      SourceType : PackageSource
      Source : string }

/// Represents a package dependency.
type PackageDependency = 
    { Defining : Package
      Referenced : Package }

/// Represents a dependency.
type Dependency = 
    | FromRoot of Package
    | FromPackage of PackageDependency
    member this.Referenced = 
        match this with
        | FromRoot p -> p
        | FromPackage d -> d.Referenced

/// Represents package details
type PackageDetails =  string * Package list

/// Interface for discovery APIs.
type IDiscovery = 
    abstract GetPackageDetails : bool * PackageSource * string * string * ResolverStrategy * string -> Async<PackageDetails>
    abstract GetVersions : PackageSource * string * string -> Async<string seq>

/// Represents a resolved dependency.
type ResolvedDependency = 
    | Resolved of Dependency
    | Conflict of Dependency * Dependency

/// Represents a complete dependency resolution.
type PackageResolution = {
    DirectDependencies : Map<string*string,Package list>
    ResolvedVersionMap : Map<string,ResolvedDependency>
}