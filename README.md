# Aperture High-Scale Platform & Architecture Reference

This repository contains the complete architectural blueprint and ASP.NET Core 8 reference implementation for evolving the **Aperture B2B SaaS Image Processing Platform**.

It addresses two major evolution challenges:
1. **Scenario A: Bulk Processing Pipeline** — Asynchronous ingestion of hundreds of large images, distributed multi-step pipeline orchestration, fault tolerance, and intermediate checkpointing.
2. **Scenario B: Third-Party Filter Marketplace** — Zero-trust sandboxed compute isolation (AWS Firecracker/Lambda in dedicated sandbox accounts), ephemeral pre-signed S3 data interchange contracts, immutable semantic versioning, and event-driven revenue-split metering.

---

## 📁 Repository Structure

```
.
├── Aperture_Architecture_Assessment.md   # Official Submission Document (Formatted for Google Docs)
├── README.md                             # Repository Documentation & Architectural Overview
└── Aperture.Platform/                    # ASP.NET Core 8 Web API Implementation
    ├── Models/
    │   └── ApertureModels.cs             # DTOs, Filter Contracts, Metering & Pipeline Models
    ├── Services/
    │   └── ApertureServices.cs           # S3 Pre-signed Ingestion, Step Functions Simulator & Ledger
    └── Program.cs                        # Minimal API Endpoints & Interactive Swagger UI
```

---

## 🏛️ System Architecture Summary

### Scenario A: Bulk Processing Pipeline

* **Direct-to-S3 Ingestion**: Clients obtain batch pre-signed S3 URLs via `POST /api/batches/upload-session` and upload photos directly. This completely bypasses the 6MB Lambda payload limit and 30s API Gateway timeout.
* **Orchestration**: AWS Step Functions with **Distributed Map** fans out execution across hundreds of images in parallel, coordinating sequential steps ($Filter_A \rightarrow Filter_B \rightarrow Filter_C$).
* **Checkpointing & Fault Tolerance**: Each step outputs to an ephemeral S3 scratch key (`/scratch/{batchId}/{imageId}/step_{n}.png`) backed by a 24-hour auto-expiration lifecycle policy. Failed steps can be retried independently without re-running earlier steps.
* **Non-blocking Progress Polling**: Atomic counters maintain progress in a single `Batch` aggregate entity in DynamoDB (`GET /api/batches/{batchId}/status`).

### Scenario B: Third-Party Filter Marketplace

* **Zero-Trust Sandboxing**: Untrusted third-party filters run as containerized Lambdas inside an isolated AWS Sandbox Account with no VPC access, blocked internet egress, and strict execution timeouts.
* **Ephemeral Data Contract**: Aperture generates short-lived (5-min) S3 pre-signed `GET` and `PUT` URLs. Third-party filters have zero IAM credentials and never discover bucket contents.
* **Immutable Versioning**: All filters use strict semantic versioning (`author/filter-name@1.2.0`). Pipelines pin exact versions. Deprecated versions are given a 90-day grace period.
* **Event-Driven Metering**: Filter completions trigger asynchronous events via Amazon EventBridge into an append-only DynamoDB ledger (`GET /api/marketplace/metering/ledger`), calculating a 75/25 developer payout split.

---

## 🚀 Running the Reference Implementation Locally

### Prerequisites
* [.NET 8 SDK or higher](https://dotnet.microsoft.com/download)

### Quick Start
```bash
# 1. Clone the repository
git clone <your-repo-url>
cd "New test/Aperture.Platform"

# 2. Run the Web API
dotnet run
```

Once running, navigate to `http://localhost:5000` (or the port shown in your terminal) to explore the interactive **Swagger UI**.

### Interactive API Endpoints:
* `POST /api/batches/upload-session`: Generates direct S3 upload URLs for a batch of images.
* `POST /api/batches/{batchId}/execute`: Starts a multi-step filter pipeline with simulated Step Functions orchestration.
* `GET /api/batches/{batchId}/status`: Real-time batch progress (shows completed count, failed count, and intermediate artifacts).
* `POST /api/marketplace/filters/invoke-sandbox`: Simulates isolated 3rd-party filter execution using ephemeral S3 signed URLs.
* `GET /api/marketplace/metering/ledger`: View metered compute usage and developer revenue-split calculations.

---

## 📄 Submission Document

The official submission text for the Crossover assessment is available in [`Aperture_Architecture_Assessment.md`](./Aperture_Architecture_Assessment.md). You can copy and paste its contents directly into the Google Doc submission template.
