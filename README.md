# SimBootstrap

Production-grade deployment and provisioning engine specialized for racing simulator club PCs. It installs, configures, repairs, and updates software and configurations on target devices.

## Solution Features
* **Package Manifest System**: JSON-based schema definitions for target installers (`.exe`, `.msi`, `.zip`, PowerShell, etc.).
* **WPF MVVM Client**: Clean desktop setup conditionally compiling on Windows targets while remaining compile-safe on macOS.
* **Deterministic Builds & CPM**: Centrally managed dependencies (`Directory.Packages.props`) with nullable context enabled and warnings treated as errors.

## Documentation Index
* [Architecture Design](docs/Architecture.md)
* [Development Environment Setup](docs/DevelopmentEnvironment.md)
* [Coding Guidelines](docs/CodingStandards.md)
* [Package Manifest Specification](docs/PackageManifest.md)
* [Deployment Vision](docs/DeploymentVision.md)
* [Repository Structure Directory](docs/RepositoryStructure.md)
* [Sprint 0 Foundation Scope](docs/Sprint0.md)
* [Future Project Roadmap](docs/Roadmap.md)
