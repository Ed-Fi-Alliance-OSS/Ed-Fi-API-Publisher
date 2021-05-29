# Ed-Fi API Publisher

Made possible through funding provided by the Michael & Susan Dell Foundation and the efforts of:<br/>
![](images/brought-to-you-by.png)

## Introduction
The Ed-Fi API Publisher is a utility that can be used to move all data (or just changes) from one Ed-Fi ODS API instance to another instance of the _same_ version of Ed-Fi. It operates as a standard API client against both API endpoints (source and target) and thus it does not require any special network configuration, direct ODS database access or a particular database engine. From a data security/privacy perspective, it is also subject to all authorization performed by the Ed-Fi ODS API endpoints with which it communicates.

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

To demonstrate how the API Publisher works, this exercise copies all the data from [the sample hosted Ed-Fi ODS API](https://api.ed-fi.org) to a local sandbox Ed-Fi ODS API using the API client for the "minimal" template.

### Configure Local Sandbox Environment

Before using the API Publisher on a target ODS, you must create and configure an API client with the appropriate permissions for publishing.

Create and assign a claim set for the API Publisher by running the following SQL Server database scripts:
  * [Create-API-Publisher-Writer-Security-Metadata.sql](QuickStart/SqlServer/Create-API-Publisher-Writer-Security-Metadata.sql)
  * [Configure-Minimal-Sandbox-Client-as-API-Publisher-Writer.sql](QuickStart/SqlServer/Configure-Minimal-Sandbox-Client-as-API-Publisher-Writer.sql)

### Build the API Publisher

Build the API Publisher solution by running the following command from the repository's root directory:

`dotnet build`

The API Publisher executable (`EdFiApiPublisher.exe`) will be located in the _.\EdFi.Tools.ApiPublisher.Cli\bin\Debug\netcoreapp3.1_ subfolder.

### Publish Data to Local Sandbox

Start (or restart) the local sandbox Ed-Fi ODS API.

Next, locate the key/secret for the API client for the minimal template sandbox. You can use the Sandbox Admin tool, or can just run the following query against the `EdFi_Admin` database:

```sql
SELECT  [Key], [Secret]
FROM    EdFi_Admin.dbo.ApiClients
WHERE   Name = 'Minimal Demonstration Sandbox'
```

Execute the Ed-Fi API Publisher using the command-line with the following arguments:
```
.\EdFiApiPublisher.exe
    --sourceUrl=https://api.ed-fi.org/v5.2/api/
    --sourceKey=RvcohKz9zHI4
    --sourceSecret=E1iEFusaNf81xzCxwHfbolkC
    --targetUrl=http://localhost:54746/
    --targetKey=minimal_sandbox_API_key
    --targetSecret=minimal_sandbox_API_secret
    --force=true
    --includeDescriptors=true
    --excludeResources=surveys
```
NOTE: The `--excludeResources` flag is used to prevent trying to move any survey data due to an issue with the security metadata (described in [ODS-4974](https://tracker.ed-fi.org/browse/ODS-4974)) in the Ed-Fi ODS API v5.2 release. If you remove this argument, the publishing operation will fail due to unsatisfied dependencies in the data.

## Known Limitations / Issues

Currently the Ed-Fi API Publisher has the following known limitations:

* API resource items deleted in source API cannot be published to the target API due to limitations of the current Change Queries implementation in the Ed-Fi ODS API.
* Even with delete support added by exposing the primary key values, tracking and publishing deletions of Descriptors will still not be possible due to internal implementation details within the API.
* Changes to primary keys in source API resources will currently result in stale copies of the "old" version of the resources (and all impacted dependencies) remaining in the target API.
* Student/Staff/Parent UniqueId changes in the source API could result in the inability of the Ed-Fi API Publisher to continue publishing to the target API.
* Profiles (for defining resource/property level data policies for API clients) are not yet supported by the Ed-Fi API Publisher.

Feedback on the need for resolution to these issues should be provided to the Ed-Fi Alliance through [Ed-Fi Tracker](https://tracker.ed-fi.org/).

More technical details on some of these issues can be found [here](Documentation/Known-Issues-Details.md).

## Next Steps

When you're ready to look further, review these other topics:

* [API Connection Management](Documentation/API-Connection-Management.md)
* [API Publisher Configuration](Documentation/API-Publisher-Configuration.md)
* [Considerations for API Hosts](Documentation/Considerations-for-API-Hosts.md)

## Legal Information

Copyright (c) 2020 Ed-Fi Alliance, LLC and contributors.

Licensed under the [Apache License, Version 2.0](LICENSE) (the "License").

Unless required by applicable law or agreed to in writing, software distributed
under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
CONDITIONS OF ANY KIND, either express or implied. See the License for the
specific language governing permissions and limitations under the License.

See [NOTICES](NOTICES.md) for additional copyright and license notifications.
