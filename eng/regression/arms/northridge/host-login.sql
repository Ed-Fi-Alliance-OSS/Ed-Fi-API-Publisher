-- SPDX-License-Identifier: Apache-2.0
-- Licensed to the Ed-Fi Alliance under one or more agreements.
-- The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
-- See the LICENSE and NOTICES files in the project root for more information.

-- Host SQL Server: the SQL login the api-source container uses to read EdFi_Ods_Northridge. Idempotent; a re-run
-- sets the password to the value in northridge.env so the connection string the bootstrap stores always matches.
-- Variables (sqlcmd -v, from Start-NorthridgeSource.ps1): host_user, host_password, host_db.
-- db_owner mirrors the login the laptop runbook used; the API only reads, but it also creates its own helper
-- objects on first start and db_owner keeps that out of the way.

SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE [name] = N'$(host_user)')
    CREATE LOGIN [$(host_user)] WITH PASSWORD = N'$(host_password)', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [master];
ELSE
    ALTER LOGIN [$(host_user)] WITH PASSWORD = N'$(host_password)', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;
GO

USE [$(host_db)];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name] = N'$(host_user)')
    CREATE USER [$(host_user)] FOR LOGIN [$(host_user)];
IF IS_ROLEMEMBER('db_owner', N'$(host_user)') = 0
    ALTER ROLE [db_owner] ADD MEMBER [$(host_user)];
GO

SELECT sp.[name] AS LoginName, dp.[name] AS DatabaseUser, r.[name] AS DatabaseRole
FROM sys.server_principals sp
JOIN sys.database_principals dp ON dp.[sid] = sp.[sid]
JOIN sys.database_role_members m ON m.member_principal_id = dp.principal_id
JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
WHERE sp.[name] = N'$(host_user)';
