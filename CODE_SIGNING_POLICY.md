# Code Signing Policy — WolfTeam Diriliş

**Status: draft for review; signing is not enabled.** This policy prepares the maintained Windows bootstrap,
launcher and installer for a future SignPath review. It does not assert that the current application is fully
audited, that a certificate has been issued, or that SignPath has accepted this project.

## Scope and exclusions

Only executables freshly compiled from the reviewed repository commit by the approved GitHub-hosted Windows
workflow may be considered:

| Component | Source | Candidate filename |
|---|---|---|
| Bootstrap | `launcher/bootstrap/*.cs`, `launcher/csharp/BootLayout.cs` | `WolfTeamDirilis.exe` |
| Launcher | `launcher/csharp/*.cs` | `WolfTeamLauncher.exe` |
| Installer | `launcher/setup/*.cs`, shared Theme/BootLayout | `WolfTeamDirilis-Setup.exe` |

Never submit the original or modified game client (`Wolfteam.bin`), game DLLs, anti-cheat components, XFS/REZ
archives, extracted game graphics/music, third-party installers, existing release EXEs, unknown binaries, test
executables or a whole game distribution. A familiar filename is not proof of source or ownership.

The preparation build has no game-derived icon/background and contains no game payload. Its endpoints use
the reserved `build-only.invalid` domain. Its assemblies explicitly identify themselves as preparation builds.
These outputs are **unsigned, non-operational qualification artifacts, not player releases**. Do not replace
the live installer, launcher manifest or download package with them. Passing tests does not authorize signing.

## Ownership, licensing and project eligibility

The public repository is [qaptanjes/wolf2team-launcher](https://github.com/qaptanjes/wolf2team-launcher).
Its maintained source is distributed under the existing [MIT license](LICENSE). That license does not grant
rights to proprietary game components. Before submitting an OSS application, the owner must confirm source
authorship and third-party notices for every component in scope, provide the necessary source/build information,
and obtain SignPath acceptance.
Do not relicense third-party game assets or advertise an existing SignPath Foundation sponsorship.

SignPath Foundation's OSS requirements include open-source licensing and prohibit proprietary bundled
components. A private/commercial SignPath arrangement has different prerequisites. Neither eligibility nor
approval is established by this CI setup. The launcher downloads and starts external game files in normal
operation; disclose that behavior and its provenance/licensing to SignPath even though those files are excluded
from these artifacts. Do not use signing to disguise unreviewed payloads or evade security products.

## Build and provenance requirements

1. Build an exact reviewed commit on a GitHub-hosted `windows-2025` runner. Keep actions pinned to reviewed
   full commit SHAs. No `pull_request_target`, self-hosted runner, downloaded build script or external binary input.
2. Use the Windows-provided .NET Framework x64 compiler, C# 5 and framework libraries; no project NuGet restore
   or proprietary local asset dependency. Record compiler hash/version and runner image version.
3. Start from a clean Git checkout and an empty build directory. Run launcher/bootstrap/setup tests, endpoint
   validation, artifact inspection and isolated install/repair/uninstall tests. A failure blocks artifact publication.
4. Verify the exact artifact and embedded-resource allowlists. Installer resources must match the newly built
   bootstrap/launcher and the checked-in preparation data by SHA256. No existing EXE can be supplied to the build.
5. Publish an unsigned GitHub Actions artifact with `SHA256SUMS`, `build-report.json`, source input hashes, commit
   and workflow run ID. Retain relevant logs. The current report deliberately sets `eligibleForSigning:false`.
   These records provide traceability; the legacy compiler build is **not claimed byte-for-byte reproducible**.

The workflow has read-only repository permission, no signing secret, no release upload and no deployment step.
Local builds are useful test evidence but are not a substitute for SignPath's trusted GitHub artifact origin.

## Required approval before activating signing

- Repository owner/maintainer: `qaptanjes` (ownership administration; this is not an approval already granted).
- Security reviewer and release approver: **not yet assigned**. Assign accountable people before activation;
  the author of a change must not be its sole production-signing approver.
- Protect release branches and signing workflow changes with review requirements and CODEOWNERS. Configure a
  protected GitHub environment with required reviewers and no self-approval. Restrict SignPath submitter rights,
  certificate use, repository/workflow/ref and GitHub-hosted build policy. Review third-party action changes.
- Complete review of updater trust, downloaded executable provenance, HTTPS endpoints, path traversal/reparse
  boundaries, process startup, credential handling and installer deletion behavior. Inspect current Defender/AV
  findings without disabling protection or silently uploading private software to a public scanning service.
- Configure the SignPath organization/project, correct artifact configurations, certificate and approval policy.
  Store a least-privilege submission token in the protected environment; never in Git, logs or downloadable files.
- Request explicit release approval for the exact source SHA, artifact hashes, test run and approved payload.

There is intentionally **no executable signing workflow** in this change. Merely setting a repository variable or
secret cannot turn the preparation workflow into a signer. `ops/signpath/submit-step.yml.example` is documentation
only and requires a separately reviewed activation change after the above gates.

## Signing order and verification

The C# installer embeds the two application EXEs as .NET resources. Signing only the outer setup does not sign
those embedded executables. A future approved pipeline must:

1. Compile/test the reviewed bootstrap and launcher, upload their artifact to GitHub, and submit that artifact ID
   using SignPath's GitHub connector with the explicit `launcher-artifacts` configuration.
2. Wait for SignPath approval. Verify returned Authenticode signatures, expected publisher/certificate policy,
   timestamps and artifact origin. Never replace the inputs with unrelated local EXEs.
3. Package those exact approved signed outputs in the installer; re-inspect resources and repeat installer tests.
   Record a new installer hash. This signed-input packaging path is **not implemented or enabled in preparation**.
4. Submit the resulting installer under the separate `installer-artifact` configuration and verify its signature
   and the embedded signed applications. Recompute published checksums after signing, since signing changes bytes.
5. Release only after approval; retain source SHA, workflow run, unsigned/signed hashes and SignPath request IDs.

The sample XML uses exact filenames rather than wildcards. Local allowlist tests supplement SignPath
configuration: an artifact configuration must not be assumed to reject every unlisted file by itself. Validate
the XML against the schema provided by the actual SignPath organization before enabling it.

## Incident handling

On unexpected payloads, malware findings, compromised credentials or disputed source provenance: stop submission
and publication, preserve build/SignPath audit evidence, notify the owner and designated security reviewer, and
investigate the affected source/artifacts. Revoke/rotate compromised submission credentials through the account
owner. Coordinate certificate revocation with the certificate issuer when needed; do not silently re-sign a
suspect executable. Publish a corrective notice only after the affected release and remedy are established.

## Verified primary documentation (2026-10-22)

- [SignPath GitHub integration and origin checks](https://docs.signpath.io/trusted-build-systems/github)
- [Artifact configuration syntax](https://docs.signpath.io/artifact-configuration/syntax)
- [Supported artifact formats](https://docs.signpath.io/artifact-configuration/reference)
- [SignPath Foundation conditions](https://signpath.org/terms)
- [GitHub Windows 2025 runner inventory](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md)

No third-party service enrollment, terms acceptance or certificate request has been performed by this change.
