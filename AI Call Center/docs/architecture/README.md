# Architecture Documentation Guide

This directory explains how the AI Call Center repository is organized, what belongs in each folder, and which parts of the system may depend on or communicate with each other.

Read the documents in this order:

1. [Repository Folder Catalog](./REPOSITORY_FOLDER_CATALOG.md) — the purpose and expected contents of every major folder.
2. [Backend Modules and Dependencies](./BACKEND_MODULES_AND_DEPENDENCIES.md) — module ownership, layer responsibilities, references, and prohibited dependencies.
3. [Frontend and BFF Relationships](./FRONTEND_AND_BFF_RELATIONSHIPS.md) — React, Redux Toolkit, RTK Query, BFF endpoints, sessions, and realtime updates.
4. [Runtime and Integration Flows](./RUNTIME_AND_INTEGRATION_FLOWS.md) — call, appointment, Open Dental, MQTT, notification, and audit interactions.

These documents are normative architecture guidance. When the implementation intentionally differs, record the decision in `docs/adr/` and update the affected documentation in the same change.

## How to use this guide when adding a file

Before creating a file, answer:

1. Which business capability owns this behavior or data?
2. Is the file domain logic, application orchestration, an external implementation, a contract, or an entry-point concern?
3. Is it backend server state, frontend server-state access, or frontend-only UI state?
4. Does another module need the behavior, or only a published fact about its outcome?
5. Does the dependency direction comply with the rules documented here?
6. Does the file process sensitive data, require authorization, generate audit records, or need retention rules?

If ownership is unclear, do not put the file in `SharedKernel`, `components`, or `services` as a shortcut. Resolve the boundary first.

## Terminology

- **Host:** An executable process and composition root, such as the Web BFF or a worker.
- **Module:** A business capability with explicit ownership and an internal layered boundary.
- **Adapter:** An implementation translating between PurpleGlass contracts and an external technology/provider.
- **Contract:** A deliberately exposed, versioned input, output, command, event, or DTO shape.
- **Projection/read model:** Data shaped for queries or UI display and derived from authoritative records/events.
- **BFF:** Backend for Frontend; the browser-specific server boundary used by the React dashboard.
- **Vertical:** Industry-specific policies and vocabulary that extend platform capabilities without changing the generic call engine.

