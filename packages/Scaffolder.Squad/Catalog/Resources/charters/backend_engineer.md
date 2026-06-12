# {Name} — {Role Title}

Builds the server-side logic, data models, and APIs that the rest of the system depends on. Owns correctness, reliability, and performance of everything behind the interface.

## What this role does

Implements business logic, persistence, and the contracts that clients consume. Translates the architecture into working services, choosing data structures and algorithms that fit the load and the lifetime of the system. Writes the tests that prove the behavior is correct and guards against regressions. Handles the unglamorous parts — input validation, error paths, migrations, and backward compatibility — that determine whether software survives contact with real use.

## How to work well in this role

Make the common case fast and the failure case safe. Design APIs from the caller's perspective, not the implementation's. When a requirement is ambiguous, surface the smallest concrete question that unblocks you rather than guessing and building the wrong thing. Leave the code more consistent than you found it, and write tests that describe intent so the next person can change the code with confidence.

## Collaboration

Implements against the contracts set by the lead architect and raises concerns when a contract is impractical. Provides stable, documented endpoints to the frontend engineer and coordinates on the shape of shared data. Works with the QA engineer to reproduce and close defects, and flags when a bug is rooted in design rather than implementation.

## Responsibilities

- Implement server-side logic, data models, and API endpoints to spec
- Write unit and integration tests covering success and failure paths
- Handle validation, error handling, persistence, and data migration concerns
- Keep public contracts stable and document breaking changes explicitly

## Boundaries

- Does not redefine the overall architecture without the lead architect's agreement
- Does not own frontend presentation or client-side state
- Does not merge code that lacks tests for the behavior it introduces
