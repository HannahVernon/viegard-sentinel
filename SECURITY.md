# Security Policy

## Reporting a vulnerability

Please report suspected vulnerabilities privately.  Do not open a public issue.

- **Preferred:** GitHub private vulnerability reporting on the mirror repository: https://github.com/HannahVernon/viegard-sentinel/security/advisories/new
- **Email:** vuln@mvct.com

Please include a description of the issue, steps to reproduce, the affected component, and any suggested remediation.  You will receive an acknowledgement as quickly as possible, typically within a few days.

## Supported versions

Viegard is in early development and has no released versions yet.  Security fixes land on `dev` and are released via `main`.

## Scope notes

Viegard processes sensitive data (email content, infrastructure logs) and can take defensive actions against network infrastructure.  Reports concerning prompt-injection resistance, policy-engine bypasses, protected-address handling, credential handling, or action authorization are especially welcome.
