# Contributing to PCleaner

Keep issues, pull requests, documentation, code comments, and user-facing text in English.

## Report an issue

Include your Windows version, PCleaner version, affected browser or application version, reproduction steps, and the expected and actual results. For cleanup problems, include the affected rule name and a redacted error from the Activity log.

Remove account identifiers, personal paths, document contents, cookie values, and other private data from logs and screenshots. Never attach a complete browser profile or Local Storage backup to a public issue.

## Make a change

1. Fork the repository and create a focused branch.
2. Follow the [build guide](docs/BUILDING.md) to build and run the tests on Windows.
3. Keep the change focused and update the relevant documentation.
4. Open a pull request describing the problem, resulting behavior, and validation performed.

Use existing C# and XAML conventions. The build treats warnings as errors. UI changes should include screenshots captured with disposable demonstration data.

## Changes to cleanup rules

Cleanup behavior needs particular care because it can remove user data. When adding or changing a rule:

- Identify the precise application versions, paths, and formats it supports.
- Cite the upstream implementation or official documentation in the rule reference where available.
- Describe visible effects, elevation needs, and application-lock requirements.
- Preserve protected data and add focused tests that prove the relevant boundary.
- Use synthetic or sanitized fixtures and temporary directories. Validate destructive behavior only in a disposable environment.

Do not broaden a target just to remove a validation failure. Unknown formats should be reported or skipped. See [safety and scope](docs/SAFETY.md) and the existing `SafetyGuard` tests.

## Licensing

PCleaner is distributed under the [MIT License](LICENSE). Ensure you have permission to contribute your changes. Third-party assets and dependencies must retain their applicable license and attribution; update [third-party notices](THIRD_PARTY_NOTICES.md) when adding or changing them.