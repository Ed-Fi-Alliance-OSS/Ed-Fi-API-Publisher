-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Admin bootstrap for the Northridge source stack (northridge.yml). Runs against EdFi_Admin in the db-admin
-- container through sqlcmd; idempotent. Variables (sqlcmd -v) come from northridge.env via Start-NorthridgeSource.ps1:
--   host_server, host_user, host_password, host_db   how the API reaches EdFi_Ods_Northridge on the host
--   client_key, client_secret, claimset               the API client the items use as the publisher's source
--   edorg1, edorg2                                    the LEA and ESC present in the backup (255901, 255900)
-- The target side is arm B's own Admin database; nothing here touches it.

SET NOCOUNT ON;

INSERT INTO [dbo].[OdsInstances] ([Name], [InstanceType], [ConnectionString])
SELECT 'northridge', 'ODS', 'Server=$(host_server);User Id=$(host_user);Password=$(host_password);Database=$(host_db);Application Name=EdFi.Ods.WebApi;Encrypt=false;TrustServerCertificate=true'
WHERE NOT EXISTS (SELECT 1 FROM [dbo].[OdsInstances] WHERE [Name] = 'northridge');

-- A re-run with a changed host login or password updates the stored connection string.
UPDATE [dbo].[OdsInstances]
SET [ConnectionString] = 'Server=$(host_server);User Id=$(host_user);Password=$(host_password);Database=$(host_db);Application Name=EdFi.Ods.WebApi;Encrypt=false;TrustServerCertificate=true'
WHERE [Name] = 'northridge'
AND [ConnectionString] <> 'Server=$(host_server);User Id=$(host_user);Password=$(host_password);Database=$(host_db);Application Name=EdFi.Ods.WebApi;Encrypt=false;TrustServerCertificate=true';

INSERT INTO [dbo].[Vendors] ([VendorName])
SELECT 'Regression Northridge Vendor'
WHERE NOT EXISTS (SELECT 1 FROM [dbo].[Vendors] WHERE [VendorName] = 'Regression Northridge Vendor');

INSERT INTO [dbo].[VendorNamespacePrefixes] ([NamespacePrefix], [Vendor_VendorId])
SELECT p.Prefix, v.[VendorId]
FROM [dbo].[Vendors] v
CROSS JOIN (VALUES ('uri://ed-fi.org'), ('uri://northridge.edu')) p(Prefix)
WHERE v.[VendorName] = 'Regression Northridge Vendor'
AND NOT EXISTS (SELECT 1 FROM [dbo].[VendorNamespacePrefixes] x WHERE x.[NamespacePrefix] = p.Prefix AND x.[Vendor_VendorId] = v.[VendorId]);

INSERT INTO [dbo].[Applications] ([ApplicationName], [OperationalContextUri], [Vendor_VendorId], [ClaimSetName])
SELECT 'Regression Northridge Source', 'uri://ed-fi.org', v.[VendorId], '$(claimset)'
FROM [dbo].[Vendors] v
WHERE v.[VendorName] = 'Regression Northridge Vendor'
AND NOT EXISTS (SELECT 1 FROM [dbo].[Applications] WHERE [ApplicationName] = 'Regression Northridge Source');

INSERT INTO [dbo].[ApplicationEducationOrganizations] ([EducationOrganizationId], [Application_ApplicationId])
SELECT e.EdOrgId, ap.[ApplicationId]
FROM [dbo].[Applications] ap
CROSS JOIN (VALUES ($(edorg1)), ($(edorg2))) e(EdOrgId)
WHERE ap.[ApplicationName] = 'Regression Northridge Source'
AND NOT EXISTS (SELECT 1 FROM [dbo].[ApplicationEducationOrganizations] x
                WHERE x.[EducationOrganizationId] = e.EdOrgId AND x.[Application_ApplicationId] = ap.[ApplicationId]);

INSERT INTO [dbo].[ApiClients] ([Key], [Secret], [Name], [IsApproved], [UseSandbox], [SandboxType], [SecretIsHashed], [Application_ApplicationId])
SELECT '$(client_key)', '$(client_secret)', 'Regression Northridge Source Client', 1, 0, 0, 0, ap.[ApplicationId]
FROM [dbo].[Applications] ap
WHERE ap.[ApplicationName] = 'Regression Northridge Source'
AND NOT EXISTS (SELECT 1 FROM [dbo].[ApiClients] WHERE [Name] = 'Regression Northridge Source Client');

INSERT INTO [dbo].[ApiClientApplicationEducationOrganizations] ([ApiClient_ApiClientId], [ApplicationEducationOrganization_ApplicationEducationOrganizationId])
SELECT ac.[ApiClientId], aeo.[ApplicationEducationOrganizationId]
FROM [dbo].[ApiClients] ac
JOIN [dbo].[ApplicationEducationOrganizations] aeo ON aeo.[Application_ApplicationId] = ac.[Application_ApplicationId]
WHERE ac.[Name] = 'Regression Northridge Source Client'
AND NOT EXISTS (SELECT 1 FROM [dbo].[ApiClientApplicationEducationOrganizations] x
                WHERE x.[ApiClient_ApiClientId] = ac.[ApiClientId]
                AND x.[ApplicationEducationOrganization_ApplicationEducationOrganizationId] = aeo.[ApplicationEducationOrganizationId]);

INSERT INTO [dbo].[ApiClientOdsInstances] ([ApiClient_ApiClientId], [OdsInstance_OdsInstanceId])
SELECT ac.[ApiClientId], oi.[OdsInstanceId]
FROM [dbo].[ApiClients] ac
JOIN [dbo].[OdsInstances] oi ON oi.[Name] = 'northridge'
WHERE ac.[Name] = 'Regression Northridge Source Client'
AND NOT EXISTS (SELECT 1 FROM [dbo].[ApiClientOdsInstances] x
                WHERE x.[ApiClient_ApiClientId] = ac.[ApiClientId] AND x.[OdsInstance_OdsInstanceId] = oi.[OdsInstanceId]);

SELECT ac.[Name] AS ApiClient, ac.[Key], ap.[ClaimSetName], oi.[Name] AS OdsInstance
FROM [dbo].[ApiClients] ac
JOIN [dbo].[Applications] ap ON ap.[ApplicationId] = ac.[Application_ApplicationId]
JOIN [dbo].[ApiClientOdsInstances] acoi ON acoi.[ApiClient_ApiClientId] = ac.[ApiClientId]
JOIN [dbo].[OdsInstances] oi ON oi.[OdsInstanceId] = acoi.[OdsInstance_OdsInstanceId]
WHERE ac.[Name] = 'Regression Northridge Source Client';
