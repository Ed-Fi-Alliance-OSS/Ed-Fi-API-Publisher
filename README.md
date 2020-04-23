# Ed-Fi API Publisher
Made possible through funding provided by the Michael & Susan Dell Foundation and the efforts of:<br/>
![](images/brought-to-you-by.png)
## Introduction
The Ed-Fi API Publisher is a utility that can be used to move data from one Ed-Fi ODS API v3.x instance to another. It operates as a standard API client against both API endpoints (source and target) and thus it does not require any special network configuration or direct ODS database access, and is also subject to all authorization performed by the Ed-Fi ODS API endpoints with which it communicates.

Operationally, it can be used in a "Pull" model where it is deployed alongside a target (central) API and gathers data from multiple source APIs.
<br/>
![](images/pull-central.png)

Alternatively, it could also be used in a "Push" model where the utility is deployed alongside the source APIs and pushes data to the central target.
<br/>
![](images/push-central.png)

However, it can also be used in a "Publishing" model where it is installed alongside a source API and pushes data to multiple targets (e.g. a State Education Agency and a collaborative).
<br/>
![](images/publish.png)

If a source API supports the "Change Queries" feature, the Ed-Fi API Publisher will perform a full publishing of the source data on the first run, and then will only publish changed data to the target on subsequent runs. The change versions that have been published are maintained in the configuration data automatically for each source/target combination.

## Known Limitations
Currently the Ed-Fi API Publisher has the following known limitations:
* API resource items deleted in source API cannot currently be published to the target API due to limitations of the current Change Queries implementation in the Ed-Fi ODS API.
* Even with delete support added by exposing the primary key values, tracking and publishing deletions of Descriptors will still not be possible due to internal implementation details within the API.
* Changes to primary keys in source API resources will currently result in stale copies of the "old" version of the resources (and all impacted dependencies) remaining in the target API.
* Student/Staff/Parent UniqueId changes in the source API could result in the inability of the Ed-Fi API Publisher to continue publishing to the target API.
* Profiles (for defining resource/property level data policies for API clients) are not yet supported by the Ed-Fi API Publisher.
* Configuration support is only currently provided for:
    * SQL Server
    * Amazon AWS (using Systems Manager Parameter Store).

Discussions are ongoing with the Ed-Fi Alliance surrounding possible solutions to these issues. Some solutions may be developed and  contributed back to the main Ed-Fi repositories.

## Considerations for Source API Hosts
### Provide Snapshot Isolation for Client Publishing/Synchronization
In order to provide a reliable environment for any API clients (including the Ed-Fi API Publisher) performing publishing/synchronization operations, it is _highly recommended_ that source APIs provide a mechanism for API clients to perform their processing against a static copy of the ODS data that is isolated from ongoing changes in the underlying ODS database as this can lead to inconsistencies and the failure to complete the publishing process successfully. Even worse, it could even result in undetected lost data (or changed data) in the target API.

In order to provide an isolated context for client change processing, the host must perform the steps below.
#### Deploy the "Publishing" Extension for the Ed-Fi ODS API
The "Publishing" extension does the following:
* Adds a new database table (`publishing.Snapshot`) to capture basic information about the available snapshots.
* Adds a new "snapshots" API resource (at _/data/v3/publishing/snapshots_) which enables API clients to read the snapshot identifier of the available snapshot (or snapshots) which they then supply with each API request using the `Snapshot-Identifier` HTTP header.
* Adds API support for processing the `Snapshot-Identifier` HTTP header to service API requests using the corresponding static ODS rather than the main ODS.
#### Implement DevOps Processes for Maintaining Static Copy of the ODS
The host must implement the processes to maintain a periodically refreshed static copy of the API's main ODS database, and the corresponding records in the `publishing.Snapshot` table. This would generally be implemented as a basic scheduled database backup and restore operation, but it could also be implemented using SQL Server's lighter weight "Database Snapshots" feature when using a server-based installation of SQL Server (as opposed to a cloud-based offering like SQL Azure or Amazon RDS).

The host's process must perform the following steps:
* Backup up the current EdFi_Ods database (or equivalent).
* Generate a "snapshot identifier" as a string-based representation of a GUID value (with no dashes).
* Restore the ODS database using following naming convention: *{Ed-Fi ODS database name}_SS{guid-no-dashes}*.
* Insert a new record into the `publishing.Snapshot` table with the new snapshot identifier and the current date/time.

The host's process _should_ also perform the following steps:
* Drop old snapshot databases. (Note: Hosts may choose to maintain the last 2 snapshot databases to avoid dropping a database that could be currently in use by an API client. If that were to happen, the client would begin receiving `410 Gone` responses from the API indicating they need to start over with their processing and synchronize using a newer snapshot).
* Remove corresponding records from `publishing.Snapshot` table for the dropped snapshots.

### Define Authorization and Security Metadata
* Create an "Ed-Fi API Publisher" (or otherwise appropriately named) claim set that provides _read_ permissions to the API resources needed for the use case. The claim set must be created by modifying data directly in the _EdFi_Security_ database.
* Create an Application in the Admin app/database for the Ed-Fi API Publisher, naming it meaningfully for the use case.
* Associate the Application with the "Ed-Fi API Publisher" claim set.
* Create an API client (key and secret) in the Admin app/database for use by the Ed-Fi API Publisher.
* Provide the key, secret and your API's base URL to the party responsible for configuring the Ed-Fi API Publisher's connections. The API's base URL includes everything up to, but not including, the _/data/v3_ portion.

## Considerations for Target API Hosts
### Define Authorization and Security Metadata
* Create an "Ed-Fi API Publisher" claim set that provides appropriate _read_ and _write_ permissions to the API resources needed for the use case.  The claim set must be created by modifying data directly in the _EdFi_Security_ database.
	* Read permissions will be used to perform deletions (not yet supported due to current Ed-Fi ODS API functionality).
	* Consider granting write permissions to all descriptors (i.e. the _systemDescriptors_ and _managedDescriptors_ resource claims), overriding the authorization strategy to use "No Further Authorization". This will allow the Ed-Fi API Publisher to ensure that all of a source API's supporting descriptors values will be present in the target ODS.
* Create an Application in the Admin app/database for the Ed-Fi API Publisher, naming it meaningfully for the use case.
* Create an API client (key and secret) in the Admin app/database to be used by the Ed-Fi API Publisher to write data _on behalf of_ a particular source API.
* Associate the Application with the "Ed-Fi API Publisher" claim set.
* Provide the key, secret and your API's base URL to the party responsible for configuring the Ed-Fi API Publisher's connections. The API's base URL includes everything up to, but not including, the _/data/v3_ portion.

## Ed-Fi API Publisher Configuration
The Ed-Fi API Publisher provides a hierarchical organization of configuration information, as documented below.

The first layer of configuration values are provided by the _publisherSettings.json_ file, which should reside in the same folder as the Ed-Fi API Publisher's binaries. This file contains the general Options and the AuthorizationFailureHandling configuration information. The Options values can also be supplied using environment variables or command-line arguments, as needed. For configuration of authorization failure handling, only the JSON settings file can be used (this information should rarely, if ever, need to change).

Command-line arguments take precedence over environment variables, which in turn take precedence over the values defined in the _publisherSettings.json_ configuration file. To use environment variables to provide configuration values, use the "Configuration Path" from the tables below, and add an `EdFi:Publisher:` prefix to the name of each variable. For example, to specify a named connection for the source API using an environment variable, use an environment variable name of `EdFi:Publisher:Connections:Source:Name`.

### Options
Defines general behavior of the Ed-Fi API Publisher.

| Configuration Path / Command-Line Argument | Description |
|---|---|
| Options:BearerTokenRefreshMinutes<br/>`--bearerTokenRefreshMinutes` | Indicates how frequently the Ed-Fi API Publisher will obtain a new bearer token from the source and target API endpoints.<br/>(_Default value: 12_)|
| Options:PostRetryStartingDelayMilliseconds<br/>`--retryStartingDelayMilliseconds` | Indicates the initial delay in milliseconds used when performing an exponential "back off" delay for retries.<br/>(_Default value: 200_) |
| Options:MaxPostRetryAttempts<br/>`--maxRetryAttempts` | Indicates the total number of times the Ed-Fi API Publisher will attempt to POST a request against the target API before determining that the failure is permanent.<br/>(_Default value: 5_) |
| Options:MaxDegreeOfParallelismForPostResourceItem<br/>`--maxDegreeOfParallelismForPostResourceItem` | Indicates the total number of parallel threads that could be simultaneously issuing POST requests against the target API.<br/>(_Default value: 200_) |
| Options:MaxDegreeOfParallelismForStreamResourcePages<br/>`--maxDegreeOfParallelismForStreamResourcePages` | Indicates the total number of resources that could be processed simultaneously, assuming all dependencies have been satisfied.<br/>(_Default value: 10_) |
| Options:StreamingPagesWaitDurationSeconds<br/>`--streamingPagesWaitDurationSeconds` | Indicates the number of seconds to wait for the streaming of any of the currently streaming resources to complete before providing an update on progress using the logger.<br/>(_Default value: 10_)|
| Options:StreamingPageSize<br/>`--streamingPageSize` | Indicates the number of items to include in each page when streaming resources from the source API.<br/>(_Default value: 75_) |
| Options:IncludeDescriptors<br/>`--includeDescriptors` | Indicates whether or not to attempt to publish descriptors.<br/>(_Default value: false_)|
| Options:ErrorPublishingBatchSize<br/>`--errorPublishingBatchSize` | Indicates the number of items to batch in each call to the error writer. This could be used to optimize the size of a batch write depending on the operating environment (e.g. Amazon DynamoDB allows for 25 items to be written in a BatchWriteItem operation).<br/>(_Default value: 25_) |

### API Connections
The recommended approach is to use pre-configured named API connections that are stored in a persistent configuration source. The Ed-Fi API Publisher defines an `INamedApiConnectionDetailsReader` interface for obtaining this configuration information, allowing for customization of this behavior by a developer.

Currently, an implementation is provided that reads this information from the [AWS Systems Manager Parameter Store](https://docs.aws.amazon.com/systems-manager/latest/userguide/systems-manager-parameter-store.html). The configuration needed for managing a named connection in AWS Systems Manager is described below.

|  Parameter Name | Type |  Description |
|---|---|---|
| /ed-fi/publisher/connections/_{connection-name}_/url | String | The base URL of the Ed-Fi ODS API (up to, but not including, the _/data/v3_ portion of the URL). |
| /ed-fi/publisher/connections/_{connection-name}_/key | SecureString | The key to use for API authentication. |
| /ed-fi/publisher/connections/_{connection-name}_/secret | SecureString | The secret to use for API authentication. |
| /ed-fi/publisher/connections/_{connection-name}_/force | String | (_Optional_) A boolean value (true/false) indicating whether the source Ed-Fi ODS API data should be published even if it does not support an isolated context through the use of database snapshots (see the "Publishing" extension described earlier in this document for more details). |
| /ed-fi/connector/connections/_{connection-name}_/resources | String | _(Optional)_ For _source_ API connections, the domain resources (excluding descriptors, which are handled separately) to publish to the target. The value is defined using a CSV format (comma-separated values), and should contain the partial paths to the resources (e.g. _/ed-fi/students_,_/custom/busRoutes_). For convenience when working with Ed-Fi standard resources, only the name is required (e.g. _students,studentSchoolAssociations_). The Ed-Fi API Publisher will also evaluate and automatically include all dependencies of the requested resources (using the dependency metadata exposed by the target API). This will ensure (barring misconfigured authorization metadata or data policies) that data can be successfully published to the target API. |
| /ed-fi/connector/connections/_{connection-name}_/lastChangeVersionsProcessed | String | _(Optional)_ For _source_ API connections, contains a JSON object, keyed by target API name, that indicates the last change version successfully published from the source to the target. This value is automatically created/updated by the Ed-Fi API Publisher after successfully completing the publishing process. |

Generally, just the source and target API names should be provided to the Ed-Fi API Publisher. However, if it is necessary to supply the connection information explicitly, the values can be provided using environment variables or command-line arguments (as documented below). 

NOTE: If the Ed-Fi API Publisher is executed using explicit connection information (rather than a pre-configured _named_ connection), the LastChangeVersionProcessed value cannot be updated automatically upon successful publishing (as there is no API connection name associated with the information). It will be the responsibility of the caller to update the value appropriately after extracting the new change version from the log output (or through some other enterprising manner). As such, for implementing a process that is intended to only publish _changes_ from a source to a target, it is impractical to use an approach where the API connection details is provided explicitly at execution time.

To select or supply source and target connection information, the following configuration values apply:

| Configuration Path | Description |
|---|---|
| Connections:Source:Name<br/>`--sourceName` | The name of the preconfigured connection for the source Ed-Fi ODS API. |
| Connections:Source:Url<br/>`--sourceUrl` | The URL of the source Ed-Fi ODS API. _Only required if named connections are not in use._ |
| Connections:Source:Key<br/>`--sourceKey` | The API key for authenticating with the source Ed-Fi ODS API. _Only required if named connections are not in use._ |
| Connections:Source:Secret<br/>`--sourceSecret` | The API secret for authenticating with the source Ed-Fi ODS API. _Only required if named connections are not in use._ |
| Connections:Source:Scope<br/>`--sourceScope` | (_Optional_) The EducationOrganizationId scope requested for the resulting access token. The value must be an EducationOrganizationId that is explicitly associated with the API client by the source Ed-Fi ODS API.<br/><br/>Intended for use to allow a single API connection configuration to be used to read changes from the controlling organization's Ed-Fi ODS API, but with the operations of the Ed-Fi API Publisher authorized for a particular Education Organization. |
| Connection:Source:Resources<br/>`--resources` | (_Optional_) For _source_ API connections, the resources to publish to the target. The value is defined using a CSV format (comma-separated values), and should contain the partial paths to the resources (e.g. _/ed-fi/students_,_/custom/busRoutes_). For convenience when working with Ed-Fi standard resources, only the name is required (e.g. _students,studentSchoolAssociations_). The Ed-Fi API Publisher will also evaluate and automatically include all dependencies of the requested resources (using the dependency metadata exposed by the target API). This will ensure (barring misconfigured authorization metadata or data policies) that data can be successfully published to the target API. |
| Connections:Source:Force<br/>`--force` | (_Optional_) A boolean value (true/false) indicating whether the source Ed-Fi ODS API data should be published even if it does not support an isolated context through the use of database snapshots (see the "Publishing" extension described earlier in this document for more details). |
| Connections:Source:LastChangeVersionProcessed<br/>`--lastChangeVersionProcessed` | _(Optional)_ Indicates the last change version successfully published from the _source_ API, and thus the change version _after_ which the current publishing operation should start. _This value explicitly overrides any change version value obtained from a named connection._ |
| Connections:Target:Name<br/>`--targetName` | The name of the preconfigured connection for the target Ed-Fi ODS API. |
| Connections:Target:Url<br/>`--targetUrl` | The URL of the target Ed-Fi ODS API. _Only required if named connections are not in use._ |
| Connections:Target:Key<br/>`--targetKey` | The API key for authenticating with the target Ed-Fi ODS API. _Only required if named connections are not in use._ |
| Connections:Target:Secret<br/>`--targetSecret` | The API secret for authenticating with the target Ed-Fi ODS API. _Only required if named connections are not in use._ |
| Connections:Target:Scope<br/>`--targetScope` | (_Optional_) The EducationOrganizationId scope requested for the resulting access token. The value must be an EducationOrganizationId that is explicitly associated with the API client by the target Ed-Fi ODS API.<br/><br/>Intended for use to allow a single API connection configuration to be used to publish changes to the controlling organization's Ed-Fi ODS API, but with the operations of the Ed-Fi API Publisher authorized for a particular Education Organization.|
| Connections:Target:TreatForbiddenPostAsWarning<br/>`--treatForbiddenPostAsWarning` | _(Optional)_ A boolean value (true/false) indicating whether `403 Forbidden` responses from `POST` requests against the connection (as a target) should be treated as a warning, rather than a failure.<br/><br/>NOTE: This option can be used in scenarios where the target API may not grant the Ed-Fi API Publisher full CRUD permissions to all the dependencies of the specified resources to be written. In such a scenario, the dependent data must already exist in the target ODS or the resulting `409 Conflict` responses will cause publishing failure. |

### Authorization Failure Handling
Defines metadata (as an array of JSON objects) about which resources could experience `403 Forbidden` responses caused by data dependencies needed for successful authorization, and which other resources should be processed before retrying the original request. For example, while an API client may be able to create a Student, they won't be able to _update_ the Student until that Student is enrolled in a School through the StudentSchoolAssociation. By defining the authorization-related dependency of the _update_ operation on the StudentSchoolAssociation, the Ed-Fi API Publisher can know to retry the failed POST request after the association has been established.

NOTE: This part of the configuration can only be defined in the _publisherSettings.json_ file. 

| JSON Path | Description |
|---|---|
| /authorizationFailureHandling\[\*] | Defines metadata for a single resource which could experience 403 Forbidden responses. |
| /authorizationFailureHandling\[\*]/path | The partial path for the resource (e.g. _/ed-fi/students_) for which additional 403 Forbidden processing should be performed. |
| /authorizationFailureHandling\[\*]/updatePrerequisitePaths | An array of partial paths for the resource(s) (e.g. _/ed-fi/studentSchoolAssociations_) that should be processed before attempting to retry the original request which resulted in an authorization failure. |

The default configuration, which will probably suffice for all current Ed-Fi ODS API v3.x deployments, is as follows:
```json
{  
  "options":   
  {  
    ...
  },  
  "authorizationFailureHandling": [  
    {  
      "path": "/ed-fi/students",  
      "updatePrerequisitePaths": ["/ed-fi/studentSchoolAssociations"]  
    },  
    {  
      "path": "/ed-fi/staffs",  
      "updatePrerequisitePaths": [  
        "/ed-fi/staffEducationOrganizationEmploymentAssociations",  
        "/ed-fi/staffEducationOrganizationAssignmentAssociations"  
      ]  
    },  
    {  
      "path": "/ed-fi/parents",  
      "updatePrerequisitePaths": ["/ed-fi/studentParentAssociations"]  
    }
  ]
}
```
## Ed-Fi API Publisher Extensibility
The Ed-Fi API Publisher has been developed using SOLID Principles of Software Development. It provides several interfaces that are pertinent for a developer wanting to change some of the behavior or out-of-the-box integrations of the software.

For example, a developer could provide alternative implementations for any of the following aspects:
* Rather than using AWS Systems Manager Parameter Store to manage the connection configurations, they could be persisted and managed using Azure App Configuration.
* Rather than simply logging the data errors that occur, the API Publisher could write these values to a structured data store like Amazon DynamoDB or Microsoft Azure Cosmos DB.
* Rather than storing the LastChangeVersionProcessed in the AWS Systems Manager Parameter Store, they could be saved to a database. NOTE: This type of customization would require some additional work to also integrate that persistent store with the .NET configuration architecture since those values are read by the Ed-Fi API Publisher as configuration values.

The following interfaces are defined, and could be useful for tailoring the Ed-Fi API Publisher to work in other contexts:
* **IErrorPublisher** - Defines a method for capturing data-related errors for subsequent analysis and troubleshooting.
* **INamedApiConnectionDetailsReader** - Defines a method for obtaining named API connection details.
* **IChangeVersionProcessedWriter** - Defines a method for setting the last change version processed for a particular source and target connection.

## Known Limitations (Details)
### Deletes Cannot Be Published (without a custom build of the ODS API)
Resources deleted in the source API cannot currently be published by the Ed-Fi API Connector due to the implementation of the Change Queries feature in the Ed-Fi ODS API. As currently implemented, the API provides a "/deletes" resource under each data management resource (e.g. _/data/v3/ed-fi/students/deletes_). However, the resource only returns two properties for each deleted item: the resource identifier and the change version. Unfortunately, resource identifiers cannot be specified by API clients upon creation (they are server-assigned values), and so as data is moved from a given source API to one or more targets, each corresponding target's resource will have its own resource identifier for the resource. Since the Ed-Fi API Publisher only has access to the source's resource identifier, no meaningful action can be taken against the target. 

There has been some discussion with the Ed-Fi Alliance about how to address this deficiency, but there is currently no timeline available for a resolution.

### Primary Key Changes Cannot Be Published
While it is generally preferred in relational database design for primary keys to be treated as immutable, with the natural key style of the Ed-Fi model, primary key value changes are inevitable for some resources (often because of the inclusion of "BeginDate" values, or similarly volatile values).

For this reason, there are some API resources that support changes to primary key values through `PUT` requests. API clients identify the resource to be updated by providing the `id` in the route. The request body is then used to supply the new key values.

The Ed-Fi ODS API supports this functionality for the following resources:
* classPeriods
* grades
* gradebookEntries
* locations
* sections
* sessions
* studentSchoolAssociations
* studentSectionAssociations

If an API client updates a primary key value as described above, the Change Queries implementation of the Ed-Fi ODS will not reflect this. The "new" resource (and all its dependencies) will be visible as new resources, but the removal of the "old" resource(s) will not. The result will be that a stale copies of all of the affected items (with the old primary key values) will be stranded in the target ODS.

### Deletes of Descriptors Cannot Be Published (without custom processing)
Since the resource `id` values are not portable between Ed-Fi ODS databases, API clients must use the primary key values to locate target resources when publishing deletes. However, the primary key of the `edfi.Descriptor` table in the ODS is an internal identity column which is also not portable, but is exposed to the client. Thus, the resources must be identified by the _alternate key_ -- `namespace` and `codeValue`. However, the Ed-Fi ODS tracks deletes using triggers which a) don't have the namespace/codeValue in context in the derived descriptor table triggers, and b) don't have the descriptor sub-type in context in the base Descriptor table trigger. The consequence is that API does not currently make it possible to publish descriptor deletions.

### Profiles Not Currently Supported
When an API Profile is defined, it introduces an intentional requirement on the part of the API client to communicate with the Ed-Fi ODS API using profile-specific content types (e.g. _application/vnd.ed-fi.{resource}.{profile}.readable+json_). The reason for this behavior is that it is important for an API client to acknowledge that they are aware that they are reading or writing only _part_ of a resource rather than operating on the resource as a _whole_.  When extra JSON data is supplied in a POST request to the Ed-Fi ODS API, the request will be processed and the extraneous data will just be ignored. Without the explicit use of the content types, unexpected data loss could result.

The Ed-Fi API Publisher does not currently automatically identify that use of a profile-based content type is required after interacting with either the source or target APIs. Thus, requests against such an API endpoint will currently fail.
