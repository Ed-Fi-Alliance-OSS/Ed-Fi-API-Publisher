## Quick Start

To demonstrate how the API Publisher works, this exercise copies all the data from [the sample hosted Ed-Fi ODS API](https://api.ed-fi.org) to a local sandbox Ed-Fi ODS API using the API client for the "minimal" template. (The database scripts are written for SQL Server.)

### Use the API Publisher

The API Publisher has three options to use the product.  The API Publisher requires [.NET 8.0](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) to run:

#### Option 1 - From binaries

 1. Download the latest published API Publisher package here:  [Ed-Fi API Publisher v1.0](https://dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_artifacts/feed/EdFi/NuGet/EdFi.ApiPublisher/overview/1.1.0).  Visit the page and click download.
 2. This will download a NuGet package to your computer.  Rename this file, `EdFi.ApiPublisher.1.1.0.nupkg`, to include .zip extension: `EdFi.ApiPublisher.1.1.0.zip`.
 3. The binary mentioned below is in the `EdFi.ApiPublisher.Win64` folder, as `EdFiApiPublisher.exe`.

#### Option 2 - From Docker images

The Docker image for the Ed-Fi API Publisher is available here: [Ed-Fi API Publisher v1.0 on Docker Hub](https://hub.docker.com/layers/edfialliance/ods-api-publisher/v1.1.0/images/sha256-4930ca34fbc71dee2fbbec09c904f980d86db536e0486f713fd03341ea5854d5?context=explore).  Use this to include in your Docker environment and alongside other components of the Ed-Fi stack.

#### Option 3 - Build the API Publisher from source code

If you would like to build the API Publisher from source, build the solution by running the following command from the repository's root directory:

`dotnet build`

The API Publisher executable (`EdFiApiPublisher.exe`) will be located in the _.\EdFi.Tools.ApiPublisher.Cli\bin\Debug\net8.0_ subfolder.

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
> NOTE for Ed-Fi ODS API v5.2 only: The `--exclude` flag is used to prevent trying to move any survey data due to an issue with the security metadata (described in [ODS-4974](https://tracker.ed-fi.org/browse/ODS-4974)) in the Ed-Fi ODS API v5.2 release. If you remove this argument, the publishing operation will fail due to unsatisfied dependencies in the data.  This has been fixed in future versions of the ODS/API platform.
