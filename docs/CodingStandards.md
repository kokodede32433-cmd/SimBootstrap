# Coding Standards and Guidelines

This document outlines the coding standards, formatting guidelines, and architectural rules for SimBootstrap.

## General Rules
* **Nullable Context**: Must be enabled in all projects. Every reference type must declare nullability explicitly.
* **Treat Warnings as Errors**: Build is configured with `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`. Unused code warnings, conflicts, or compilation flags must be resolved immediately.
* **Central Package Management (CPM)**: No package versions are allowed inside `.csproj` files. They must be registered exclusively inside `Directory.Packages.props`.

## C# Language Style
* Prefer file-scoped namespaces to reduce indentation.
* Always use explicit braces (`{ }`) for conditional blocks.
* Follow MVVM rules in the UI layer (no business logic in code-behind files).
* Prefer dependency injection over manual instantiation or service locators.
