# Ed-Fi API Publisher Release Notes - Version 1.3.0

## Overview

Version 1.3.0 of the Ed-Fi API Publisher includes significant functional improvements, bug fixes, and engineering enhancements focused on reliability, security, and developer experience.

## Summary of Functional Updates

- **Bug Fixes**: Fixed timeout issues with full min-max change version total count requests
- **Bug Fixes**: Fixed issue where API Publisher wouldn't publish when only 1 change was made 
- **Bug Fixes**: Fixed exceptions being hidden when using remediation scripts
- **New Feature**: Added `authUrl` argument for authentication URL configuration
- **New Feature**: Added namespace support for change version tracking via `lastChangeVersionProcessedNamespace` option
- **Improvements**: Enhanced dependencies URL extraction from API metadata with fallback support
- **Improvements**: Enhanced logging context for different message producers
- **Security**: Updated Docker base images and dependencies to address vulnerabilities
- **Documentation**: Fixed documentation links and improved reverse paging documentation

## Summary of Engineering Updates

### Repository Infrastructure Enhancements

- **Dependency Management**: Added automated dependency updates through GitHub Dependabot configuration
- **GitHub Workflows**: Implemented comprehensive CI/CD pipeline with automated workflows for:
  - Pull request validation
  - Release management
  - Pre-release handling
  - Issue management
  - Security scanning with OpenSSF Scorecard
  - Docker image building and validation
- **Project Organization**: Enhanced project structure with improved build scripts and configuration files
- **Documentation**: Added comprehensive documentation covering:
  - API connection management
  - Configuration options
  - Docker deployment considerations
  - Extensibility patterns
  - Known issues and remediation strategies
- **Security**: Implemented security best practices including:
  - CODEOWNERS file for repository governance
  - Issue templates for structured reporting
  - Security policy documentation
- **Build System**: Improved build automation with PowerShell scripts supporting multiple deployment targets

### Developer Experience Improvements

- **Editor Configuration**: Added consistent code formatting rules via `.editorconfig`
- **Issue Templates**: Structured issue reporting with dedicated templates for engineering issues
- **Copilot Integration**: Added GitHub Copilot instructions for enhanced development assistance
- **Docker Support**: Enhanced Docker configuration with compose files and deployment templates

## Changes in This Release

| Commit | Author | Pull Request | Description |
|--------|--------|--------------|-------------|
| [9d2542b](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/9d2542ba05687bde699d6720c46530c11daea371) | Stephen Fuqua | [#99](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/99) | Create dependabot.yml - Added comprehensive repository infrastructure including CI/CD workflows, documentation, security policies, and automated dependency management |
| [25c2d29](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/25c2d293676653a504382cd968c667ea249a9e13) | jagudelo-gap | [#97](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/97) | Issue Management Template and Action Workflow - Added issue templates, updated workflow configurations, and detailed Copilot instructions |
| [4cd13d0](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/4cd13d0bb8b23ca03b013f8b7dc58543df5a4cdb) | dfernandez-gap | [#96](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/96) | Changes in OnRelease Workflow to Delete Previous PreRelease Tags as well as GH Releases |
| [3d264f9](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/3d264f98a7cacb84e843bc4e191dc674623681af) | jleiva-gap | [#95](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/95) | Update openssl version for dockerfiles - Updated to version 3.3 |
| [a84e1ed](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/a84e1ed670b5cf92e23e781ea697a378db18afd4) | jagudelo-gap | [#94](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/94) | Fix the Step "Create API Publisher Pre-Release" |
| [98c6f80](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/98c6f80d2419ad1f1710f9e141ae2d14b96e8fdc) | DavidJGapCR | [#93](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/93) | Fixes documentation link on reverse paging |
| [f30ef4e](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/f30ef4e09c1945306dd7fc89bea12b9d08848881) | brian-pazos | [#92](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/92) | Add authUrl argument and dependencies URL from metadata - Refactored to add authUrl argument and extract dependencies URL from metadata |
| [9b0fcfd](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/9b0fcfdc537dc73636a4f2daddd1ce45da8f35bc) | jpardogrowthaccelerationpartners | [#91](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/91) | Update the deprecated packages |
| [431baf7](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/431baf788d04831eab272058612c3bb3e298c0ce) | DavidJGapCR | [#90](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/90) | API Publisher some exceptions are hidden when using a remediation script |
| [da17bee](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/da17bee5d4b2a3becb0657d487aace5b6d24956c) | ea-mtenhoor | [#89](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/89) | API Publisher some exceptions are hidden when using a remediation script |
| [7e34787](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/7e347877f410a5d6811539de8f365beb4b1795aa) | ea-mtenhoor | [#88](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/88) | Log correct context - Fixed issue where all MessageProducers were logging in the same context |
| [4be38b6](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/4be38b6c5bd4a180be26adc1d42db53d2d794f8c) | ea-mtenhoor | [#87](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/87) | Fix total count timeout and publishing with only 1 change - Fixed timeout issues and single change publishing problems |
| [c0a5461](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/c0a54612278a2cf9d1090acf5f0c5fa452f4bab2) | ea-mtenhoor | [#86](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/86) | Add namespace to change version tracking - Added lastChangeVersionProcessedNamespace option for additional uniqueness |
| [74814f5](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/74814f50f809bf982fe30dc40d03344ccbea9e17) | jasonh-edfi | [#85](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/85) | Update Reverse-Paging.md |
| [f742485](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/f742485520098d471933756917d92f94dd2c6977) | jleiva-gap | [#84](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/84) | Fixing vulnerabilities found with Docker Scout - Updated packages and Alpine version |
| [ca01994](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/ca01994a0a7d0cfdbb062346c163b46a0a9c5186) | DavidJGapCR | [#83](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/83) | Adds Sonar Analyzer - Log as json where appropriate |
| [50559ff](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/50559ff4567fddd975a14d7d2236e7a33d04da3a) | DavidJGapCR | [#82](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/82) | Adds Sonar Analyzer - log web api metadata as a json format |
| [c4cbd7c](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/c4cbd7cb7a7324e13b1241f25c581c4c29290695) | DavidJGapCR | [#81](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/81) | Fixing Actions |
| [fa51c2c](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/commit/fa51c2c521a418f724a61aaef1a6bd4fca2ef835) | DavidJGapCR | [#80](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/pull/80) | Adds Sonar Analyzer |

## Compatibility

This release maintains backward compatibility with version 1.2.1. While new features and bug fixes have been added, no breaking changes have been introduced. The new `authUrl` and `lastChangeVersionProcessedNamespace` options are optional and maintain existing behavior when not specified.

## Installation and Upgrade

### From Version 1.2.1

This release does not require any special upgrade procedures. Users can continue using existing configurations and deployment methods.

### New Installations

Follow the standard installation procedures as documented in the [README.md](../README.md) file.

## Known Issues

No new known issues have been introduced in this release. For existing known issues, please refer to the [Known Issues Details](Known-Issues-Details.md) documentation.

## Support

For support with the API Publisher, please use [Ed-Fi Support](https://support.ed-fi.org/) to open a support case and/or feature request.

---
