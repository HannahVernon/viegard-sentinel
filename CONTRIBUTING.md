# Contributing to Viegard

Thank you for your interest in Viegard.  This guide covers prerequisites, the branch model, coding standards, and pull request expectations.

## Prerequisites

- .NET 10 SDK (LTS)
- Git
- Docker (for container-based integration testing and deployment work)

Build, test, and run commands will be documented in the README once the solution skeleton exists.

## Branch model

- `main` is the release branch.
- `dev` is the integration branch and the default target for pull requests.
- Feature work goes on `feature/<short-description>` branches off `dev`; fixes go on `fix/<short-description>`.
- Never force-push or delete `main` or `dev`.

## Coding standards

- Modern C# targeting .NET 10; nullable reference types enabled; warnings are errors in CI.
- The core domain model must remain free of infrastructure dependencies.  Infrastructure code implements Viegard-owned interfaces.
- Treat all observed data (email content, log lines, URLs, User-Agents) as untrusted input.
- Never commit secrets, credentials, or real infrastructure identifiers (hostnames, IPs, network ranges).  This is a public repository.
- New dependencies require a supply-chain review (maintainer reputation, vulnerabilities, license compatibility, dependency footprint) and an entry in `THIRD-PARTY-NOTICES.md` in the same commit.  Licenses must be MIT/Apache-2.0/BSD-compatible.
- Documentation is part of the definition of done: update `README.md`, `DECISIONS.md`, `TODO.md`, and `AGENT-README.md` when behavior, architecture, or workflow changes.

## Security expectations

Viegard is a security-sensitive automation platform.  Security correctness takes precedence over feature completeness.  Read the "Security rules" section of [AGENT-README.md](AGENT-README.md) before contributing; those rules bind human contributors too.  Report vulnerabilities per [SECURITY.md](SECURITY.md); do not open public issues for them.

## Pull requests

- One logical change per PR.
- Fill in every section of the PR template.
- Include tests for new behavior and confirm the full test suite passes.
- No commented-out code or debug leftovers.
