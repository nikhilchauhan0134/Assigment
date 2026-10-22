# Aperture Architecture Assessment

---

## Scenario A: Bulk Processing Pipeline

### Important Facts

* **Gateway & Lambda limits make the current synchronous path a non-starter:** API Gateway enforces a strict 30-second connection timeout and a 6MB payload ceiling for Lambda integrations. A batch of hundreds of high-res images cannot pass through this pipe without immediately failing.
* **Lambda is time-bounded, not batch-bounded:** AWS Lambda has a hard 15-minute runtime ceiling. While processing 100 images sequentially inside a single Lambda will time out, running 100 small Lambdas in parallel easily fits within the limit.
* **S3 can ingest directly from browsers:** Clients can upload multi-gigabyte files directly to S3 via pre-signed URLs with zero compute overhead on our backend.
* **Failures in bulk image jobs are granular, not systemic:** In a batch of 500 photos, 2 or 3 might fail because of corrupted headers or extreme dimensions. Treating a batch as an all-or-nothing atomic unit wastes expensive compute and frustrates the user.
* **We must build on the existing serverless baseline:** Aperture runs on Node.js/TypeScript, DynamoDB single-table design, and S3 with a small engineering team. Bringing in Kubernetes, Kafka, or Airflow would create an operational maintenance nightmare.

---

### Important Technical Decisions

#### Decision 1: Direct-to-S3 Uploads using Batch Pre-Signed URLs
* **Problem:** Today, the browser Base64-encodes an image and sends it via JSON to API Gateway. For hundreds of multi-megabyte photos, this exhausts client memory, hits the 6MB Gateway limit, and locks browser threads.
* **Alternatives:**
  1. *Stream uploads through an EC2/ECS reverse proxy.*
  2. *Chunked uploads over WebSockets through API Gateway.*
  3. *Client-direct S3 upload using pre-signed PUT URLs.*
* **Decision:** Client-direct upload to Amazon S3 via pre-signed URLs generated in a single batch metadata call.
* **Rationale:** I chose direct-to-S3 because it takes our API completely out of the data path. The client makes one lightweight request (`POST /batches`) with a list of file sizes and checksums. Our Lambda generates 100 scoped, time-limited S3 upload URLs. The client browser uploads files directly to S3 in parallel. S3 handles TLS, streaming, and validation. Our servers handle zero image bytes during ingestion.

---

#### Decision 2: Pipeline Orchestration using AWS Step Functions (Distributed Map)
* **Problem:** We need to chain multiple filters ($Filter_A \rightarrow Filter_B \rightarrow Filter_C$) across hundreds of images. Workflows can run for 10 to 45 minutes, requiring dependable state management, parallelism, and retry controls.
* **Alternatives:**
  1. *Choreographed SQS Queues:* Queue A feeds Worker A, which writes to S3 and posts to Queue B, etc.
  2. *Self-hosted Workflow Engine (Temporal.io on ECS).*
  3. *AWS Step Functions with Distributed Map.*
* **Decision:** AWS Step Functions with Distributed Map.
* **Rationale:** SQS queue chaining creates "invisible architecture." Tracking where an image is in a dynamic pipeline ($A \rightarrow B$ vs $A \rightarrow C \rightarrow D$) requires building a custom state machine in DynamoDB, and debugging partial failures across multiple queues is painful. Temporal is technically great but adds operational overhead (managing clusters, schema migrations, and monitoring). Step Functions Distributed Map natively handles the fan-out over hundreds of S3 objects, executes the multi-step state machine for each image, gives visual tracing out of the box, and incurs zero cost when idle.

---

#### Decision 3: Intermediate Checkpointing in S3 with 24-Hour Lifecycle Purge
* **Problem:** If a 3-step pipeline fails at Step 3 on image #85, we shouldn't re-run Step 1 and Step 2 from scratch. But keeping every intermediate image forever will blow up S3 storage bills.
* **Alternatives:**
  1. *Pipe images strictly in memory on a single worker.*
  2. *Save intermediate stage outputs permanently to S3.*
  3. *Checkpoint intermediate outputs to an ephemeral S3 scratch prefix with auto-expiring lifecycle rules.*
* **Decision:** Checkpoint intermediate outputs to an ephemeral S3 prefix (`/scratch/{batchId}/{imageId}/step_{n}.png`) backed by a 24-hour expiration rule.
* **Rationale:** In-memory streaming risks out-of-memory crashes on large images and offers zero fault tolerance if a worker restarts. Writing intermediate outputs to S3 allows Step Functions to retry *only* the specific failed step without restarting the entire pipeline. Setting an S3 lifecycle policy to delete `/scratch/` objects after 24 hours ensures we only pay for the final processed image long-term.

---

#### Decision 4: Individual Single-Purpose Lambda Workers for Each Filter
* **Problem:** Our existing monolithic "Proxy" Lambda handles routing, authentication, and image processing together. Combining distinct AI filters into one runtime causes dependency bloat and makes resource allocation inefficient.
* **Alternatives:**
  1. *Scale the existing monolithic Lambda up to 10GB RAM.*
  2. *Migrate all processing to an autoscaling Amazon ECS Fargate cluster.*
  3. *Split filters into dedicated, rightsized Lambda functions.*
* **Decision:** Dedicated single-purpose Lambda functions per filter type.
* **Rationale:** Different filters have different resource profiles. A simple thumbnail/grayscale filter runs efficiently on 512MB RAM, whereas a neural style transfer model needs 4GB to 8GB RAM. Splitting them into distinct functions lets us right-size CPU/memory per filter to optimize cost, isolates blast radiuses (a crash in Filter B doesn't affect Filter A), and avoids managing ECS container clusters.

---

#### Decision 5: Aggregate Batch Tracking in DynamoDB for Low-Overhead Polling
* **Problem:** Clients need real-time visibility into batch progress (e.g., "65/100 completed, 1 failed") without hammering DynamoDB or Step Functions APIs with polling requests.
* **Alternatives:**
  1. *Have the client poll each individual image record in DynamoDB.*
  2. *Poll the Step Functions Execution History API directly from the client.*
  3. *Maintain a single `Batch` aggregate record in DynamoDB updated via atomic counters.*
* **Decision:** Single `Batch` entity in DynamoDB updated with atomic counters, queried via lightweight client polling or WebSocket.
* **Rationale:** Direct client polling against Step Functions hits AWS rate limits (`ThrottlingException`). Polling hundreds of individual image rows burns DynamoDB Read Capacity Units. By storing a parent `Batch` item with attributes `totalImages`, `completedCount`, `failedCount`, and `status`, Step Functions tasks can perform atomic updates (`ADD completedCount 1`) as each image finishes. The frontend only needs to poll one small record every 3–5 seconds.

---
---

## Scenario B: Third-Party Filter Marketplace

### Important Facts

* **Third-party code is inherently untrusted:** We cannot allow external code to run inside our core VPC, access our DynamoDB tables, or inspect images belonging to other organizations.
* **Data privacy is critical for B2B SaaS:** Customers trust Aperture with proprietary creative assets. Third-party developers must not be able to store, siphon, or retain customer images outside of execution time.
* **Cold starts impact pipeline throughput:** In a batch pipeline processing hundreds of images, spinning up heavy containers from scratch per image introduces massive latency and timeout risks.
* **Pipelines require deterministic stability:** Customers configure automated pipelines that run for months. If a third-party author edits or deletes an existing filter, customer production workflows must not break.
* **Flat-rate pricing is already losing money:** The MVP baseline notes that power users are unprofitable. Introducing third-party filters without granular execution metering will quickly bankrupt our margins.

---

### Important Technical Decisions

#### Decision 1: Sandboxed Execution via Containerized Lambdas in an Isolated Sandbox AWS Account
* **Problem:** We must run untrusted third-party code securely without risking remote code execution, internal network intrusion, or noisy-neighbor interference.
* **Alternatives:**
  1. *WebAssembly (Wasm) runtime embedded in our backend workers.*
  2. *Webhooks (Third-party developers host their own HTTP endpoints).*
  3. *Containerized AWS Lambda deployed in an isolated, dedicated AWS Sandbox Account.*
* **Decision:** Containerized AWS Lambda deployed in an isolated AWS Sandbox Account.
* **Rationale:** 
  * Wasm is safe and fast, but it heavily restricts third parties from using standard AI/ML runtimes like PyTorch, OpenCV, and native Python/C++ libraries.
  * Webhooks force customer photos to leave Aperture's security perimeter, creating major privacy and compliance violations, while introducing latency and uptime vulnerabilities we cannot control.
  * Containerized Lambdas give developers standard Docker environments to build whatever they want. Running them in a dedicated AWS account separate from Aperture Core provides defense-in-depth: zero access to our DynamoDB, zero internal VPC connectivity, blocked internet egress, and strict execution timeouts (e.g., 45s).

---

#### Decision 2: Ephemeral Zero-Trust Data Contract via Short-Lived Pre-Signed S3 URLs
* **Problem:** How do third-party filters receive input images and write outputs without having direct IAM credentials or access to our S3 storage?
* **Alternatives:**
  1. *Grant the third-party Lambda an IAM role with S3 bucket read/write permissions.*
  2. *Pass raw image bytes Base64-encoded in the invocation JSON payload.*
  3. *Pass short-lived (5-minute) pre-signed S3 `GET` (input) and `PUT` (output) URLs in the invocation payload.*
* **Decision:** Short-lived Pre-Signed S3 URLs with a strict JSON execution contract.
* **Rationale:** Granting IAM roles to third-party code violates the principle of least privilege. Passing Base64 images hits the 6MB Lambda payload limit. Pre-signed URLs grant time-bounded access *only* to the specific input photo and *only* to a designated output destination key. The third-party filter has zero AWS credentials, cannot discover bucket contents, and cannot retain access after execution finishes.

---

#### Decision 3: Semantic Versioning with Immutable Artifacts & 90-Day Deprecation Window
* **Problem:** If a marketplace author releases an update with breaking behavior or removes an older filter version, enterprise batch workflows will fail unexpectedly.
* **Alternatives:**
  1. *Mutable filter tags (always point to `latest`).*
  2. *Immutable SemVer tags (`filter-name@1.2.0`) with guaranteed retention.*
* **Decision:** Immutable Semantic Versioning with a 90-day grace period for deprecated versions.
* **Rationale:** Every published version is built into an immutable container image tagged with a specific version (`org/filter@major.minor.patch`). Once published, a version can never be overwritten. When a customer adds a filter to their pipeline, the pipeline pins the exact version. If a developer deprecates a version, it is hidden from the marketplace for new users, but existing customer pipelines remain operational for 90 days with deprecation notices shown in the dashboard.

---

#### Decision 4: Asynchronous Metering and Billing via EventBridge & DynamoDB Ledger
* **Problem:** We need to accurately bill customers per filter execution and pay revenue share to developers (e.g., 75% to developer / 25% to Aperture) without slowing down the image processing loop or failing jobs when billing services hiccup.
* **Alternatives:**
  1. *Synchronous billing API call (e.g., Stripe) inside the filter execution step.*
  2. *Parse and aggregate CloudWatch logs at the end of the month.*
  3. *Asynchronous event emission via EventBridge into a DynamoDB billing ledger.*
* **Decision:** Asynchronous Event-Driven Metering via Amazon EventBridge and DynamoDB.
* **Rationale:** Calling an external payment processor inside the image execution loop adds 300–800ms of latency per image and introduces an external point of failure. CloudWatch log parsing is delayed and unreliable. Instead, when a filter step finishes, the orchestrator emits a lightweight event to EventBridge: `{ orgId, filterId, developerId, version, executionTimeMs, timestamp }`. A decoupled consumer writes this to an append-only DynamoDB ledger. This guarantees sub-millisecond overhead on the image pipeline, accurate dispute resolution, and automated end-of-month payout calculations.
