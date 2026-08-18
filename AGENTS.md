Project Type

This repository is a development tooling project. Apply the global development/tooling contract except where the tool itself is the system under test.

Tooling-Specific Rules

Do not blindly apply consumer-project orchestration rules to the tool being developed.

For the stack:

RimTest -> RimContext -> DevBridge2

When developing RimTest, use RimTest's declared bootstrap/self-test workflow rather than assuming an installed RimTest validates changed RimTest source.
When developing RimContext or DevBridge2, direct execution of that component is allowed when required by its repository test workflow.
Respect each layer's ownership; do not move responsibilities between layers merely to work around a failure.
Treat structured schemas, statuses, error codes, nextAction, identifiers, and freshness semantics as integration contracts.
Changes to cross-layer behavior should receive integration coverage with adjacent layers or representative consumers.

Use the repository's own bootstrap and validation instructions as authoritative for testing the tool itself.