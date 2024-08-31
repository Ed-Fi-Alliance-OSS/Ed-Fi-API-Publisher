---
sidebar_position: 0
---

## Introduction
The Ed-Fi API Publisher is a utility that can be used to move data and changes from one Ed-Fi ODS API instance to another instance of the _same_ version of Ed-Fi. It operates as a standard API client against both API endpoints (source and target) and thus it does not require any special network configuration, direct ODS database access or a particular database engine. From a data security/privacy perspective, it is also subject to all authorization performed by the Ed-Fi ODS API endpoints with which it communicates.

Operationally, it can be used in a "Pull" model where it is deployed alongside a target (central) API and gathers data from multiple source APIs.
<br/>
![](/img/pull-central.png)

Alternatively, it could also be used in a "Push" model where the utility is deployed alongside the source APIs and pushes data to the central target.
<br/>
![](/img/push-central.png)

However, it can also be used in a "Publishing" model where it is installed alongside a source API and pushes data to multiple targets (e.g. to both a State Education Agency and a collaborative).
<br/>
![](/img/publish.png)

If a source API supports the "Change Queries" feature, the Ed-Fi API Publisher will perform a full publishing of the source data on the first run, and then will only publish changed data to the target on subsequent runs. The change versions that have been published to a particular target are maintained in a configuration store automatically for each source/target combination.

## Next Steps

When you're ready to look further, review these other topics:

* [API Connection Management](docs/API-Connection-Management.md)
* [API Publisher Configuration](docs/API-Publisher-Configuration.md)
* [Considerations for API Hosts](docs/Considerations-for-API-Hosts.md)
* [Considerations for Docker Configuration and Execution](docs/Considerations-docker-configuration-and-execution.md)
* [How to use the TextFormatter class](docs/Use-TextFormatter-Serilog.md).

## Support

For support with the API Publisher, please use [Ed-Fi Support](https://support.ed-fi.org/) to open a support case and/or feature request.

## Legal Information

Copyright (c) 2024 Ed-Fi Alliance, LLC and contributors.

Licensed under the [Apache License, Version 2.0](LICENSE) (the "License").

Unless required by applicable law or agreed to in writing, software distributed
under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
CONDITIONS OF ANY KIND, either express or implied. See the License for the
specific language governing permissions and limitations under the License.

See [NOTICES](NOTICES.md) for additional copyright and license notifications.