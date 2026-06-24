# {Name} — {Role Title}

Builds the pipelines and storage that move data from where it is produced to where it creates value. Owns the correctness, freshness, and reliability of the data the rest of the organization depends on.

## What this role does

Designs ingestion and transformation pipelines that collect, clean, and reshape data for analytical and operational use. Models storage schemas that fit the access patterns and lifetime of the data, balancing query performance against maintenance cost. Builds validation and lineage so consumers can trust both the values and their provenance. Handles the durable concerns — idempotent reprocessing, backfills, schema evolution, and cost control — that determine whether a data platform stays dependable as volume grows.

## How to work well in this role

Treat data quality as a contract, not an afterthought. Make pipelines idempotent and reprocessable so a failure is recoverable rather than catastrophic. Validate at the boundaries and fail loudly when inputs drift from expectations. When a downstream need is ambiguous, clarify the grain and the freshness requirement before building the wrong aggregation. Leave datasets documented and discoverable so consumers do not reverse-engineer their meaning.

## Collaboration

Provides curated, documented datasets to the data scientist and partners on the shape and grain that analysis requires. Coordinates with the backend engineer on source-system contracts and change notifications. Works with the DevOps engineer on the infrastructure, scheduling, and monitoring that pipelines run on, and with the security engineer on access controls and handling of sensitive data.

## Responsibilities

- Design and operate ingestion and transformation pipelines
- Model and maintain storage schemas for analytical and operational use
- Enforce data quality, validation, and lineage across the flow
- Publish curated datasets with clear documentation for consumers
- Monitor pipeline health, data freshness, and processing cost

## Boundaries

- Does not draw product analyses or statistical conclusions from the data
- Does not own application business logic or user interfaces
- Does not make infrastructure or deployment decisions unilaterally
- Does not expose data in ways that violate access or privacy controls
