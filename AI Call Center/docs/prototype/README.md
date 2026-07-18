# Prototype 1 Delivery Guide

This series is the working plan for the first executable PurpleGlass prototype. It converts the broader architecture into a small, testable delivery path.

## Prototype objective

Prove that the selected architecture works end to end before adding telephony, AI, Open Dental, or real patient data:

```text
React dashboard
    → Web BFF
    → tenant-scoped application query/command
    → PostgreSQL transaction and outbox
    → MQTT event
    → sanitized BFF realtime update
    → Redux/RTK Query UI refresh
```

The prototype uses synthetic tenants and locations only.

## Reading and execution order

1. [Scope and Success Criteria](./01_SCOPE_AND_SUCCESS_CRITERIA.md)
2. [Implementation Roadmap](./02_IMPLEMENTATION_ROADMAP.md)
3. [Foundation Checklist](./03_FOUNDATION_CHECKLIST.md)
4. [First Vertical Slice](./04_FIRST_VERTICAL_SLICE.md)
5. [Testing, Acceptance, and Demo](./05_TESTING_ACCEPTANCE_AND_DEMO.md)
6. [Prototype Backlog](./06_PROTOTYPE_BACKLOG.md)

## How to use the series

- Complete phases in order unless an ADR explicitly changes a dependency.
- Do not begin the next phase until the current exit gate passes.
- Mark checklist items only after automated verification or recorded manual evidence.
- Keep commits small and aligned with one backlog item or acceptance gate.
- Update these documents whenever implementation reveals an incorrect assumption.
- Record consequential technology or boundary decisions in `docs/adr/`.

## Status vocabulary

| Status | Meaning |
|---|---|
| Not started | No implementation work has begun |
| In progress | Work is active but the exit gate does not pass |
| Blocked | A named external decision or dependency prevents progress |
| Complete | Deliverables exist and the documented exit gate passes |

Initial prototype status: **In progress**. Phase 0 and the Phase 1 backend skeleton are implemented; local infrastructure is next.
