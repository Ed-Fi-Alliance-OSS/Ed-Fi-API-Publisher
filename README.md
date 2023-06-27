# Ed-Fi API Publisher

## Introduction
The Ed-Fi API Publisher is a utility that can be used to move data and changes from one Ed-Fi ODS API instance to another instance of the _same_ version of Ed-Fi. It operates as a standard API client against both API endpoints (source and target) and thus it does not require any special network configuration, direct ODS database access or a particular database engine. From a data security/privacy perspective, it is also subject to all authorization performed by the Ed-Fi ODS API endpoints with which it communicates.

Operationally, it can be used in a "Pull" model where it is deployed alongside a target (central) API and gathers data from multiple source APIs.
<br/>
![](images/pull-central.png)

Alternatively, it could also be used in a "Push" model where the utility is deployed alongside the source APIs and pushes data to the central target.
<br/>
![](images/push-central.png)

However, it can also be used in a "Publishing" model where it is installed alongside a source API and pushes data to multiple targets (e.g. to both a State Education Agency and a collaborative).
<br/>
![](images/publish.png)

If a source API supports the "Change Queries" feature, the Ed-Fi API Publisher will perform a full publishing of the source data on the first run, and then will only publish changed data to the target on subsequent runs. The change versions that have been published to a particular target are maintained in a configuration store automatically for each source/target combination.

## Quick Start

To demonstrate how the API Publisher works, this exercise copies all the data from [the sample hosted Ed-Fi ODS API](https://api.ed-fi.org) to a local sandbox Ed-Fi ODS API using the API client for the "minimal" template. (The database scripts are written for SQL Server.)

### Configure Local Sandbox Environment

Before using the API Publisher on a target ODS, you must create and configure an API client with the appropriate permissions for publishing.

Create and assign a claim set for the API Publisher by running the following database scripts:
  * [Create-API-Publisher-Writer-Security-Metadata.sql](eng/Create-API-Publisher-Writer-Security-Metadata.sql)
  * [Configure-Minimal-Sandbox-Client-as-API-Publisher-Writer.sql](eng/Configure-Minimal-Sandbox-Client-as-API-Publisher-Writer.sql)

### Build the API Publisher

Build the API Publisher solution by running the following command from the repository's root directory:

`dotnet build`

The API Publisher executable (`EdFiApiPublisher.exe`) will be located in the _.\EdFi.Tools.ApiPublisher.Cli\bin\Debug\netcoreapp3.1_ subfolder.

### Publish Data to Local Sandbox

> **IMPORTANT: After changing security metadata for the API, YOU MUST RESTART the local sandbox Ed-Fi ODS API if it is already running.**

Next, locate the key/secret for the API client for the minimal template sandbox. You can use the Sandbox Admin tool, or can just run the following query against the `EdFi_Admin` database:

```sql
SELECT  [Key], [Secret]
FROM    EdFi_Admin.dbo.ApiClients
WHERE   Name = 'Minimal Demonstration Sandbox'
```

The following table shows the command-line arguments that will be used for publishing.

> NOTE: Due to the nature of the Quick Start configuration (assuming SQL Server and the Ed-Fi-ODS API are running on a local development machine), we'll limit the parallelism for POST requests to `5`. Architectures with dedicated API and database resources should be able to accommodate much higher numbers.

| Parameter                                        |     | Value                             |
| ------------------------------------------------ | --- | --------------------------------- |
| `--sourceUrl`                                    | `=` | `https://api.ed-fi.org/v5.2/api/` |
| `--sourceKey`                                    | `=` | `RvcohKz9zHI4`                    |
| `--sourceSecret`                                 | `=` | `E1iEFusaNf81xzCxwHfbolkC`        |
| `--targetUrl`                                    | `=` | `http://localhost:54746/`         |
| `--targetKey`                                    | `=` | (Minimal Sandbox API _key_)       |
| `--targetSecret`                                 | `=` | (Minimal Sandbox API _secret_)    |
| `--ignoreIsolation`                              | `=` | `true`                            |
| `--maxDegreeOfParallelismForPostResourceItem`    | `=` | `5`                               |
| `--maxDegreeOfParallelismForStreamResourcePages` | `=` | `3`                               |
| `--includeDescriptors`                           | `=` | `true`                            |
| `--exclude`                                      | `=` | `surveys`                         |

Run the Ed-Fi API Publisher from the folder containing all the binaries by executing the following command, substituting your own API client's key and secrets.  (Below development keys as shown in other Ed-Fi examples):
```
.\EdFiApiPublisher.exe --sourceUrl=https://api.ed-fi.org/v5.2/api/ --sourceKey=RvcohKz9zHI4 --sourceSecret=E1iEFusaNf81xzCxwHfbolkC --targetUrl=http://localhost:54746/ --targetKey=minimal_sandbox_API_key --targetSecret=minimal_sandbox_API_secret --ignoreIsolation=true --maxDegreeOfParallelismForPostResourceItem=5 --maxDegreeOfParallelismForStreamResourcePages=3 --includeDescriptors=true --exclude=surveys
```
> NOTE: The `--exclude` flag is used to prevent trying to move any survey data due to an issue with the security metadata (described in [ODS-4974](https://tracker.ed-fi.org/browse/ODS-4974)) in the Ed-Fi ODS API v5.2 release. If you remove this argument, the publishing operation will fail due to unsatisfied dependencies in the data.

## Known Limitations / Issues

Currently the Ed-Fi ODS API has the following known issues related to Change Queries and the Ed-Fi API Publisher:

* [Change Queries implementation doesn't provide enough information to communicate deletes between ODS databases](https://tracker.ed-fi.org/browse/ODS-3672)
* [Add support to Change Queries for tracking deletes by natural key](https://tracker.ed-fi.org/browse/ODS-4423)
* [Change Queries does not capture deletes on derived resources](https://tracker.ed-fi.org/browse/ODS-4087)
* [Change Queries does not support primary key changes](https://tracker.ed-fi.org/browse/ODS-5005)

(Feedback on the need for resolution to the Ed-Fi ODS API issues listed above should be provided to the Ed-Fi Alliance through [Ed-Fi Support](https://support.ed-fi.org/).)

The Ed-Fi ODS/API only exposes the "Id" of the resources that are deleted, however since the "Id" is not intended to be a global, portable identifier for the resource (Ed-Fi uses domain key values for that identity), and thus the _current implementation_ of the deletes resource is of limited value for API Publishing.

Even with delete support added by exposing the primary key values, tracking and publishing deletions of Descriptors will still not be possible due to internal implementation details within the Ed-Fi ODS API through (at least) v5.3.

Changes to primary keys (on the API resources that support it) in source API will currently result in stale copies of the "old" version of the resources (and all impacted dependencies) remaining in the target API. 

An additional limitation of the Ed-Fi API Publisher is the current lack of support for API Profiles (for defining resource/property level data policies for API clients). Create a support case to request Profiles support if this of interest to you.

More technical details on some of these issues can be found [here](docs/Known-Issues-Details.md).

## Next Steps

When you're ready to look further, review these other topics:

* [API Connection Management](docs/API-Connection-Management.md)
* [API Publisher Configuration](docs/API-Publisher-Configuration.md)
* [Considerations for API Hosts](docs/Considerations-for-API-Hosts.md)

## Support

For support with the API Publisher, please use [Ed-Fi Support](https://support.ed-fi.org/) to open a support case and/or feature request.

## Legal Information

Copyright (c) 2023 Ed-Fi Alliance, LLC and contributors.

Licensed under the [Apache License, Version 2.0](LICENSE) (the "License").

Unless required by applicable law or agreed to in writing, software distributed
under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
CONDITIONS OF ANY KIND, either express or implied. See the License for the
specific language governing permissions and limitations under the License.

See [NOTICES](NOTICES.md) for additional copyright and license notifications.
