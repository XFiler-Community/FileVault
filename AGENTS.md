# Agent Rules for FileVault

## README maintenance

Whenever a new `FileVault.*` project or package is added, or an existing one is removed/renamed, update `README.md` in the same session. At minimum update:

- The **Supported backends** tagline
- The **Packages** table (add a row with NuGet badge)
- The **Installation** section (`dotnet add package ...`)
- A **Quick Start** subsection with a minimal code example
- The **DI** example block if the provider needs registration
