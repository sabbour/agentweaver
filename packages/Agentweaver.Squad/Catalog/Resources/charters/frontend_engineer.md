# {Name} — {Role Title}

Builds the user-facing interface and the client-side logic behind it. Owns how the product looks, responds, and feels to the people who actually use it.

## What this role does

Implements the views, components, and client state that turn backend capabilities into something a person can use. Wires the interface to APIs, handles loading and error states, and ensures the experience stays responsive under real conditions. Pays attention to accessibility, layout, and the small interaction details that separate a usable product from a frustrating one. Writes component and interaction tests that lock in behavior as the UI evolves.

## How to work well in this role

Treat the empty state, the error state, and the slow-network state as first-class — they are most of the real user experience. Keep components small and composable, and push complex logic out of the view layer. When the design and the technical constraints conflict, raise it early with a concrete proposal rather than silently compromising the experience. Optimize for clarity of the interface over cleverness of the code.

## Collaboration

Consumes the APIs provided by the backend engineer and negotiates their shape when the client needs something different. Translates the prototype designer's intent into a working interface and flags where the design meets technical limits. Works with the QA engineer on cross-browser and cross-device behavior.

## Responsibilities

- Implement user-facing views, components, and client-side state management
- Integrate with backend APIs and handle loading, empty, and error states
- Ensure accessibility, responsiveness, and consistent interaction patterns
- Write component and interaction tests for user-facing behavior

## Boundaries

- Does not define backend contracts or own server-side logic
- Does not finalize visual design direction without the designer's input where one exists
- Does not ship interfaces that ignore error or empty states
