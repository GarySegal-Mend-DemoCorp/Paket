What is the Lockfile?
=====================

Consider the following [Packages file](packages_file.html):

    source "http://nuget.org/api/v2"

    nuget "Castle.Windsor-log4net" "~> 3.2"
    nuget "Rx-Main" "~> 2.0"

Here we specify dependencies on the standard NuGet feed's `Castle.Windsor-log4net` and `Rx-Main` packages; both these packages have dependencies on further NuGet packages. 

The `packages.lock` file records the concrete resolutions of all direct *and indirect* dependencies of your project:-

    [lang=textfile]
    NUGET
      remote: http://nuget.org/api/v2
      specs:
        Castle.Core (3.3.0)
        Castle.Core-log4net (3.3.0)
          Castle.Core (>= 3.3.0)
          log4net (1.2.10)
        Castle.LoggingFacility (3.3.0)
          Castle.Core (>= 3.3.0)
          Castle.Windsor (>= 3.3.0)
        Castle.Windsor (3.3.0)
          Castle.Core (>= 3.3.0)
        Castle.Windsor-log4net (3.3.0)
          Castle.Core-log4net (>= 3.3.0)
          Castle.LoggingFacility (>= 3.3.0)
        Rx-Core (2.2.5)
          Rx-Interfaces (>= 2.2.5)
        Rx-Interfaces (2.2.5)
        Rx-Linq (2.2.5)
          Rx-Interfaces (>= 2.2.5)
          Rx-Core (>= 2.2.5)
        Rx-Main (2.2.5)
          Rx-Interfaces (>= 2.2.5)
          Rx-Core (>= 2.2.5)
          Rx-Linq (>= 2.2.5)
          Rx-PlatformServices (>= 2.2.5)
        Rx-PlatformServices (2.3)
          Rx-Interfaces (>= 2.2)
          Rx-Core (>= 2.2)
        log4net (1.2.10)

Subsequent runs of [paket install](packet_install.htm) will not reanalyze the `packages.fsx` file.

As a result, committing the `packages.lock` file to your version control system guarantees that other developers and/or build servers will always end up with a reliable and consistent set of packages regardless of where or when a [paket install](packet_install.htm) occurs.
