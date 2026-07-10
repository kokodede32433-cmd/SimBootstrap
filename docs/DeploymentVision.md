# Deployment Vision

This document details the long-term vision of how SimBootstrap acts as a deployment coordinator.

## Lightweight Management Flow
```mermaid
flowchart LR
    Cloud["Cloud Hub / CRM"] -->|Sync Manifests| SimBootstrap["SimBootstrap PC Client"]
    SimBootstrap -->|Install / Update| OS["Windows Simulator OS"]
    SimBootstrap -->|Report Progress| Cloud
```

## Operations Scope
* **Club Rollouts**: When a club PC boots, SimBootstrap runs as a startup task to pull packages, verify local state integrity, and deploy dependencies silently.
* **Auto-Repair**: Verification rules check if files are missing or modified (due to user tampering). In cases of damage, auto-repair routines trigger re-downloads and silent installations.
