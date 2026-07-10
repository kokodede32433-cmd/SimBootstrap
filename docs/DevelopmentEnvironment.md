# Development Environment Specification

This document details the software development environment, targeted runtimes, and local configuration templates.

## Developer Workstations
* **Primary**: macOS (Apple Silicon or Intel) using Rider or Visual Studio Code.
* **Secondary**: Windows 11 Pro utilizing Visual Studio 2022.

## Target Platform
* **Operating System**: Windows 11 Pro 23H2 or later.
* **Runtime**: .NET 9.0 SDK and Runtime.
* **GUI Engine**: Windows Presentation Foundation (WPF).

## Build Setup
Executables must run `dotnet build SimBootstrap.sln` cleanly on macOS platforms (excluding WPF assemblies from compile target lists).
